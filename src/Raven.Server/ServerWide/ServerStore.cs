﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lucene.Net.Search;
using Raven.Client.Documents.Conventions;
using Raven.Client.Util;
using Raven.Client.Exceptions.Database;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Server.Operations;
using Raven.Server.Commercial;
using Raven.Server.Config;
using Raven.Server.Documents;
using Raven.Server.NotificationCenter;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Context;
using Raven.Server.ServerWide.LowMemoryNotification;
using Raven.Server.Utils;
using Sparrow.Binary;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron;
using Sparrow.Logging;
using Voron.Data.Tables;
using Voron.Exceptions;

namespace Raven.Server.ServerWide
{
    /// <summary>
    /// Persistent store for server wide configuration, such as cluster settings, database configuration, etc
    /// </summary>
    public class ServerStore : IDisposable
    {
        private readonly CancellationTokenSource _shutdownNotification = new CancellationTokenSource();

        public CancellationToken ServerShutdown => _shutdownNotification.Token;

        private static Logger _logger;

        private StorageEnvironment _env;

        private readonly NotificationsStorage _notificationsStorage;



        public readonly RavenConfiguration Configuration;
        private readonly RavenServer _ravenServer;
        public readonly DatabasesLandlord DatabasesLandlord;
        public readonly NotificationCenter.NotificationCenter NotificationCenter;

        public static LicenseStorage LicenseStorage { get; } = new LicenseStorage();

        private readonly TimeSpan _frequencyToCheckForIdleDatabases;

        public ServerStore(RavenConfiguration configuration, RavenServer ravenServer)
        {
            var resourceName = "ServerStore";

            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            Configuration = configuration;
            _ravenServer = ravenServer;
            _logger = LoggingSource.Instance.GetLogger<ServerStore>(resourceName);
            DatabasesLandlord = new DatabasesLandlord(this);

            _notificationsStorage = new NotificationsStorage(resourceName);

            NotificationCenter = new NotificationCenter.NotificationCenter(_notificationsStorage, resourceName, ServerShutdown);

            DatabaseInfoCache = new DatabaseInfoCache();

            _frequencyToCheckForIdleDatabases = Configuration.Databases.FrequencyToCheckForIdle.AsTimeSpan;

        }

        public DatabaseInfoCache DatabaseInfoCache { get; set; }

        public TransactionContextPool ContextPool;

        public ClusterStateMachine Cluster => _engine.StateMachine;
        public string NodeTag => _engine.Tag;

        private Timer _timer;
        private RachisConsensus<ClusterStateMachine> _engine;

        public ClusterTopology GetClusterTopology(TransactionOperationContext context)
        {
            return _engine.GetTopology(context);
        }

        public async Task AddNodeToClusterAsync(string nodeUrl)
        {
            await _engine.AddToClusterAsync(nodeUrl);
        }

        public void Initialize()
        {
            AbstractLowMemoryNotification.Initialize(ServerShutdown, Configuration);

            if (_logger.IsInfoEnabled)
                _logger.Info("Starting to open server store for " + (Configuration.Core.RunInMemory ? "<memory>" : Configuration.Core.DataDirectory.FullPath));

            var path = Configuration.Core.DataDirectory.Combine("System");

            var options = Configuration.Core.RunInMemory
                ? StorageEnvironmentOptions.CreateMemoryOnly(path.FullPath)
                : StorageEnvironmentOptions.ForPath(path.FullPath);

            options.SchemaVersion = 2;
            options.ForceUsing32BitsPager = Configuration.Storage.ForceUsing32BitsPager;
            try
            {
                StorageEnvironment.MaxConcurrentFlushes = Configuration.Storage.MaxConcurrentFlushes;

                try
                {
                    _env = new StorageEnvironment(options);
                }
                catch (Exception e)
                {
                    throw new DatabaseLoadFailureException("Failed to load system database " + Environment.NewLine + $"At {options.BasePath}", e);
                }
            }
            catch (Exception e)
            {
                if (_logger.IsOperationsEnabled)
                    _logger.Operations(
                        "Could not open server store for " + (Configuration.Core.RunInMemory ? "<memory>" : Configuration.Core.DataDirectory.FullPath), e);
                options.Dispose();
                throw;
            }

            BooleanQuery.MaxClauseCount = Configuration.Queries.MaxClauseCount;

            ContextPool = new TransactionContextPool(_env);


            _engine = new RachisConsensus<ClusterStateMachine>();
            _engine.Initialize(_env);

            _engine.StateMachine.DatabaseChanged += DatabasesLandlord.ClusterOnDatabaseChanged;

            _timer = new Timer(IdleOperations, null, _frequencyToCheckForIdleDatabases, TimeSpan.FromDays(7));
            _notificationsStorage.Initialize(_env, ContextPool);
            DatabaseInfoCache.Initialize(_env, ContextPool);
            LicenseStorage.Initialize(_env, ContextPool);
            NotificationCenter.Initialize();

            TransactionOperationContext context;
            using (ContextPool.AllocateOperationContext(out context))
            {
                context.OpenReadTransaction();

                foreach (var db in _engine.StateMachine.ItemsStartingWith(context, "db/", 0, int.MaxValue))
                {
                    DatabasesLandlord.ClusterOnDatabaseChanged(this, db.Item1);
                }
            }
        }

        public async Task DeleteDatabaseAsync(JsonOperationContext context, string db, bool hardDelete)
        {
            using (var putCmd = context.ReadObject(new DynamicJsonValue
            {
                ["Type"] = nameof(DeleteDatabaseCommand),
                [nameof(DeleteDatabaseCommand.DatabaseName)] = db,
                [nameof(DeleteDatabaseCommand.HardDelete)] = hardDelete,
            }, "del-cmd"))
            {
                await SendToLeaderAsync(putCmd);
            }
        }

        public async Task PutValueInClusterAsync(JsonOperationContext context, string key, BlittableJsonReaderObject val)
        {
            using (var putCmd = context.ReadObject(new DynamicJsonValue
            {
                ["Type"] = nameof(PutValueCommand),
                [nameof(PutValueCommand.Name)] = key,
                [nameof(PutValueCommand.Value)] = val,
            }, "put-cmd"))
            {
                await SendToLeaderAsync(putCmd);
            }
        }

        public async Task DeleteValueInClusterAsync(JsonOperationContext context, string key)
        {
            //TODO: redirect to leader
            using (var putCmd = context.ReadObject(new DynamicJsonValue
            {
                ["Type"] = nameof(DeleteValueCommand),
                [nameof(DeleteValueCommand.Name)] = key,
            }, "delete-cmd"))
            {
                await _engine.PutAsync(putCmd);
            }
        }

        public async Task PutEditVersioningCommandAsync(JsonOperationContext context, string databaseName, BlittableJsonReaderObject val)
        {
            //TODO: redirect to leader
            using (var editVersioningCmd = context.ReadObject(new DynamicJsonValue
            {
                ["Type"] = nameof(EditVersioningCommand),
                [nameof(EditVersioningCommand.Configuration)] = val,
                [nameof(EditVersioningCommand.Name)] = databaseName,
            }, "edit-versioning-cmd"))
            {
                var index = await _engine.PutAsync(editVersioningCmd);
                await Cluster.WaitForIndexNotification(index);
            }
        }

        public void Dispose()
        {
            if (_shutdownNotification.IsCancellationRequested)
                return;
            lock (this)
            {
                if (_shutdownNotification.IsCancellationRequested)
                    return;
                _shutdownNotification.Cancel();
                var toDispose = new List<IDisposable>
                {
                    _engine,
                    NotificationCenter,
                    DatabasesLandlord,
                    _env,
                    ContextPool
                };


                var exceptionAggregator = new ExceptionAggregator(_logger, $"Could not dispose {nameof(ServerStore)}.");

                foreach (var disposable in toDispose)
                    exceptionAggregator.Execute(() =>
                    {
                        try
                        {
                            disposable?.Dispose();
                        }
                        catch (ObjectDisposedException)
                        {
                            //we are disposing, so don't care
                        }
                    });

                exceptionAggregator.Execute(() => _shutdownNotification.Dispose());

                exceptionAggregator.ThrowIfNeeded();
            }


        }

        public void IdleOperations(object state)
        {
            try
            {
                foreach (var db in DatabasesLandlord.DatabasesCache)
                {
                    try
                    {
                        if (db.Value.Status != TaskStatus.RanToCompletion)
                            continue;

                        var database = db.Value.Result;

                        if (DatabaseNeedsToRunIdleOperations(database))
                            database.RunIdleOperations();
                    }

                    catch (Exception e)
                    {
                        if (_logger.IsInfoEnabled)
                            _logger.Info("Error during idle operation run for " + db.Key, e);
                    }
                }

                try
                {
                    var maxTimeDatabaseCanBeIdle = Configuration.Databases.MaxIdleTime.AsTimeSpan;

                    var databasesToCleanup = DatabasesLandlord.LastRecentlyUsed
                       .Where(x => SystemTime.UtcNow - x.Value > maxTimeDatabaseCanBeIdle)
                       .Select(x => x.Key)
                       .ToArray();

                    foreach (var db in databasesToCleanup)
                    {
                        // intentionally inside the loop, so we get better concurrency overall
                        // since shutting down a database can take a while
                        DatabasesLandlord.UnloadDatabase(db, skipIfActiveInDuration: maxTimeDatabaseCanBeIdle, shouldSkip: database => database.Configuration.Core.RunInMemory);
                    }

                }
                catch (Exception e)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info("Error during idle operations for the server", e);
                }
            }
            finally
            {
                try
                {
                    _timer.Change(_frequencyToCheckForIdleDatabases, TimeSpan.FromDays(7));
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private static bool DatabaseNeedsToRunIdleOperations(DocumentDatabase database)
        {
            var now = DateTime.UtcNow;

            var envs = database.GetAllStoragesEnvironment();

            var maxLastWork = DateTime.MinValue;

            foreach (var env in envs)
            {
                if (env.Environment.LastWorkTime > maxLastWork)
                    maxLastWork = env.Environment.LastWorkTime;
            }

            return ((now - maxLastWork).TotalMinutes > 5) || ((now - database.LastIdleTime).TotalMinutes > 10);
        }

        public async Task<long> TEMP_WriteDbAsync(TransactionOperationContext context, string dbId, BlittableJsonReaderObject dbDoc, long? etag)
        {
            using (var putCmd = context.ReadObject(new DynamicJsonValue
            {
                ["Type"] = nameof(TEMP_SetDatabaseCommand),
                [nameof(TEMP_SetDatabaseCommand.Name)] = dbId,
                [nameof(TEMP_SetDatabaseCommand.Value)] = dbDoc,
                [nameof(TEMP_SetDatabaseCommand.Etag)] = etag,
            }, "put-cmd"))
            {
                return await _engine.PutAsync(putCmd);
            }
        }

        public void EnsureNotPassive()
        {
            if (_engine.CurrentState == RachisConsensus.State.Passive)
            {
                _engine.Bootstarp(_ravenServer.WebUrls[0]);
            }
        }

        public Task PutCommandAsync(BlittableJsonReaderObject cmd)
        {
            return _engine.PutAsync(cmd);
        }

        public async Task SendToLeaderAsync(BlittableJsonReaderObject cmd)
        {
            while (true)
            {
                var logChange = _engine.WaitForHeartbeat();

                if (_engine.CurrentState == RachisConsensus.State.Leader)
                {
                    await _engine.PutAsync(cmd);
                    return;
                }

                var engineLeaderTag = _engine.LeaderTag;// not actually working
                try
                {
                    await SendToNodeAsync(engineLeaderTag, cmd);
                    return;
                }
                catch (Exception ex)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info("Tried to send message to leader, retrying",ex);
                }

                await logChange;
            }
        }

        private async Task SendToNodeAsync(string engineLeaderTag, BlittableJsonReaderObject cmd)
        {
            TransactionOperationContext context;
            using (ContextPool.AllocateOperationContext(out context))
            {
                context.OpenReadTransaction();

                var clusterTopology = _engine.GetTopology(context);
                string leaderUrl;
                if (clusterTopology.Members.TryGetValue(engineLeaderTag, out leaderUrl) == false)
                    throw new InvalidOperationException("Leader " + engineLeaderTag + " was not found in the topology members");

                using (var shortTermExecuter = RequestExecutor.ShortTermSingleUse(leaderUrl, "Rachis.Server", clusterTopology.ApiKey))
                    await shortTermExecuter.ExecuteAsync(new PutRaftCommand(context, cmd), context, ServerShutdown);
            }
        }

        protected internal class PutRaftCommand : RavenCommand<object>
        {
            private readonly JsonOperationContext _context;
            private readonly BlittableJsonReaderObject _command;
            public override bool IsReadRequest => false;

            public PutRaftCommand(JsonOperationContext context, BlittableJsonReaderObject command)
            {
                _context = context;
                _command = context.ReadObject(command, "Raft command");
            }

            public override HttpRequestMessage CreateRequest(ServerNode node, out string url)
            {
                url = $"{node.Url}/rachis/send";

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    Content = new BlittableJsonContent(stream =>
                    {
                        using (var writer = new BlittableJsonTextWriter(_context, stream))
                        {
                            writer.WriteObject(_command);
                        }
                    })
                };

                return request;
            }

            public override void SetResponse(BlittableJsonReaderObject response, bool fromCache)
            {
            }
        }

        public Task WaitForTopology(Leader.TopologyModification state)
        {            
            return _engine.WaitForTopology(state);
        }

        public Task WaitForState(RachisConsensus.State state)
        {
            return _engine.WaitForState(state);
        }

        public void ClusterAcceptNewConnection(TcpClient client,Stream stream)
        {
            _engine.AcceptNewConnection(client, null, stream);
        }
    }
}