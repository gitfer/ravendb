﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Esprima;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors.Specialized;
using Jint.Runtime.Interop;
using Sparrow.Extensions;
using Raven.Client.Exceptions.Documents.Patching;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron.Global;
using Constants = Raven.Client.Constants;

namespace Raven.Server.Documents.Patch
{
    public class ScriptRunner
    {
        private readonly DocumentDatabase _db;
        private readonly bool _enableClr;
        public readonly List<string> ScriptsSource = new List<string>();
        private const int DefaultStringSize = 50;

        public void AddScript(string script)
        {
            ScriptsSource.Add(script);
        }

        public class SingleRun
        {
            public List<string> DebugOutput;
            public bool DebugMode;
            public bool PutOrDeleteCalled;
            public PatchDebugActions DebugActions;

            public override string ToString()
            {
                return string.Join(Environment.NewLine, _runner.ScriptsSource);
            }

            private SingleRun()
            {
                // here just to get an instance that jurrasic
                // can use
            }

            public SingleRun(DocumentDatabase database, ScriptRunner runner, List<string> scriptsSource)
            {
                _database = database;
                _runner = runner;
                ScriptEngine = new Jint.Engine(options =>
                {
                    options.LimitRecursion(64)
                        .MaxStatements(1000) // TODO: Maxim make this configurable
                        .Strict();
                });
                ScriptEngine.SetValue("output", new ClrFunctionInstance(ScriptEngine, OutputDebug));

                ScriptEngine.SetValue("load", new ClrFunctionInstance(ScriptEngine, LoadDocument));
                ScriptEngine.SetValue("del", new ClrFunctionInstance(ScriptEngine, DeleteDocument));
                ScriptEngine.SetValue("put", new ClrFunctionInstance(ScriptEngine, PutDocument));

                ScriptEngine.SetValue("id", new ClrFunctionInstance(ScriptEngine, GetDocumentId));
                ScriptEngine.SetValue("lastModified", new ClrFunctionInstance(ScriptEngine, GetLastModified));

                foreach (var script in scriptsSource)
                {
                    try
                    {
                        ScriptEngine.Execute(script);
                    }
                    catch (ParserException e)
                    {
                        throw new JavaScriptParseException("Failed to parse: " + Environment.NewLine + script, e);
                    }
                }
            }


            private JsValue GetLastModified(JsValue self, JsValue[] args)
            {
                if(args.Length != 1)
                    throw new InvalidOperationException("id(doc) must be called with a single argument");

                if (args[0].IsNull() || args[0].IsUndefined())
                    return args[0];
                
                if(args[0].IsObject() == false)
                    throw new InvalidOperationException("id(doc) must be called with an object argument");
                
                if (args[0].AsObject()is BlittableObjectInstance doc)
                {
                    if (doc.LastModified == null)
                        return Undefined.Instance;

                    // we use UTC because last modified is in UTC
                    var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    var jsTime = doc.LastModified.Value.Subtract(epoch)
                        .TotalMilliseconds;
                    return new JsValue(jsTime);
                }
                return Undefined.Instance;
            }

            private JsValue OutputDebug(JsValue self, JsValue[] args)
            {
                if (DebugMode == false)
                    return self;

                var obj = args[0];

                if (obj.IsString())
                {
                    DebugOutput.Add(obj.ToString());
                }
                else if (obj.IsObject())
                {
                    var result = new ScriptRunnerResult(this, obj);
                    using (var jsonObj = result.TranslateToObject(_context))
                        DebugOutput.Add(jsonObj.ToString());
                }
                else if (obj.IsBoolean())
                {
                    DebugOutput.Add(obj.AsBoolean().ToString());
                }
                else if (obj.IsNumber())
                {
                    DebugOutput.Add(obj.AsNumber().ToString(CultureInfo.InvariantCulture));
                }
                else if (obj.IsNull())
                {
                    DebugOutput.Add("null");
                }
                else if (obj.IsUndefined())
                {
                    DebugOutput.Add("undefined");
                }
                else
                {
                    DebugOutput.Add(obj.ToString());
                }
                return self;
            }

            public JsValue PutDocument(JsValue self, JsValue[] args)
            {
                string changeVector = null;

                if (args.Length != 2 && args.Length != 3)
                {
                    throw new InvalidOperationException("put(id, doc, changeVector) must be called with called with 2 or 3 arguments only");
                }
                AssertValidDatabaseContext();
                AssertNotReadOnly();
                if (args[0].IsString() == false && args[0].IsNull() == false && args[0].IsUndefined() == false)
                    AssertValidId();

                var id = (args[0].IsNull() || args[0].IsUndefined()) ? null : args[0].AsString();

                if (args[1].IsObject() == false)
                {
                    throw new InvalidOperationException(
                        $"Created document must be a valid object which is not null or empty. Document ID: '{id}'.");
                }

                PutOrDeleteCalled = true;

                if (args.Length == 3)
                {
                    if (args[2].IsString())
                        changeVector = args[2].AsString();
                    else if (args[2].IsNull() == false && args[0].IsUndefined() == false)
                    {
                        throw new InvalidOperationException(
                            $"The change vector must be a string or null. Document ID: '{id}'.");
                    }
                }

                if (DebugMode)
                {
                    DebugActions.PutDocument.Add(id);
                }

                using (var reader = JsBlittableBridge.Translate(_context, args[1].AsObject(),
                    BlittableJsonDocumentBuilder.UsageMode.ToDisk))
                {
                    var put = _database.DocumentsStorage.Put(_context, id, _context.GetLazyString(changeVector), reader);
                    return put.Id;
                }
            }

            private static void AssertValidId()
            {
                throw new InvalidOperationException("The first parameter to put(id, doc, changeVector) must be a string");
            }

            public JsValue DeleteDocument(JsValue self, JsValue[] args)
            {
                if(args.Length != 1 && args.Length != 2)
                    throw new InvalidOperationException("delete(id, changeVector) must be called with at least one parameter");
                
                if(args[0].IsString() == false)
                    throw new InvalidOperationException("delete(id, changeVector) id argument must be a string");

                string id = args[0].AsString();
                string changeVector = null;

                if (args.Length == 2 && args[1].IsString())
                    changeVector = args[1].AsString();
                
                PutOrDeleteCalled = true;
                AssertValidDatabaseContext();
                AssertNotReadOnly();
                if (DebugMode)
                {
                    DebugActions.DeleteDocument.Add(id);
                }
                var result = _database.DocumentsStorage.Delete(_context, id, changeVector);
                return new JsValue(result != null);

            }

            private void AssertNotReadOnly()
            {
                if (ReadOnly)
                    throw new InvalidOperationException("Cannot make modifications in readonly context");
            }

            private void AssertValidDatabaseContext()
            {
                if (_context == null)
                    throw new InvalidOperationException("Unable to put documents when this instance is not attached to a database operation");
            }

            private JsValue GetDocumentId(JsValue self, JsValue[] args)
            {
                if(args.Length != 1)
                    throw new InvalidOperationException("id(doc) must be called with a single argument");

                if (args[0].IsNull() || args[0].IsUndefined())
                    return args[0];
                
                if(args[0].IsObject() == false)
                    throw new InvalidOperationException("id(doc) must be called with an object argument");

                var objectInstance = args[0].AsObject();

                if (objectInstance is BlittableObjectInstance doc && doc.DocumentId != null)
                    return new JsValue(doc.DocumentId);

                var jsValue = objectInstance.Get(Constants.Documents.Metadata.Key);
                if(jsValue.IsObject() == false)
                    return JsValue.Null;
                var value = jsValue.AsObject().Get(Constants.Documents.Metadata.Id);
                if(value.IsString() == false)
                    return JsValue.Null;
                return value;
            }

            private JsValue LoadDocument(JsValue self, JsValue[] args)
            {
                AssertValidDatabaseContext();

                if(args.Length != 1 || args[0].IsString()== false)
                    throw new InvalidOperationException("load(id) must be called with a single string argument");

                var id = args[0].AsString();

                if (DebugMode)
                {
                    DebugActions.LoadDocument.Add(id);
                }
                var document = _database.DocumentsStorage.Get(_context, id);
                var translated = TranslateToJs(ScriptEngine, _context, document);
                return new JsValue((ObjectInstance)translated);
            }

            public bool ReadOnly;
            private readonly DocumentDatabase _database;
            private readonly ScriptRunner _runner;
            private DocumentsOperationContext _context;

            public int MaxSteps;
            public int CurrentSteps;
            public readonly Jint.Engine ScriptEngine;

            private static void ThrowTooManyLoopIterations() =>
                throw new TimeoutException("The scripts has run for too long and was aborted by the server");

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void OnStateLoopIteration()
            {
                CurrentSteps++;
                if (CurrentSteps < MaxSteps)
                    return;
                ThrowTooManyLoopIterations();
            }

            private readonly List<IDisposable> _disposables = new List<IDisposable>();

            public void DisposeClonedDocuments()
            {
                foreach (var disposable in _disposables)
                {
                    disposable.Dispose();
                }
                _disposables.Clear();
            }

            public ScriptRunnerResult Run(DocumentsOperationContext ctx, string method, object[] args)
            {
                _context = ctx;
                if (DebugMode)
                {
                    if (DebugOutput == null)
                        DebugOutput = new List<string>();
                    if (DebugActions == null)
                        DebugActions = new PatchDebugActions();
                }
                PutOrDeleteCalled = false;
                CurrentSteps = 0;
                MaxSteps = 1000; // TODO: Maxim make me configurable
                for (int i = 0; i < args.Length; i++)
                {
                    args[i] = TranslateToJs(ScriptEngine, ctx, args[i]);
                }
                var result = ScriptEngine.Invoke(method, args);
                return new ScriptRunnerResult(this, result);
            }


#if DEBUG
            static readonly HashSet<Type> ExpectedTypes = new HashSet<Type>
            {
                typeof(int),
                typeof(long),
                typeof(double),
                typeof(bool),
                typeof(string),
            };
#endif

            public object Translate(JsonOperationContext context, object o)
            {
                return TranslateToJs(ScriptEngine, context, o);
            }

            private object TranslateToJs(Jint.Engine engine, JsonOperationContext context, object o)
            {
                BlittableJsonReaderObject Clone(BlittableJsonReaderObject origin)
                {
                    if (ReadOnly)
                        return origin;

                    // RavenDB-8286
                    // here we need to make sure that we aren't sending a value to 
                    // the js engine that might be modified by the actions of the js engine
                    // for example, calling put() mgiht cause the original data to change 
                    // because we defrag the data that we looked at. We are handling this by
                    // ensuring that we have our own, safe, copy.
                    var cloned = origin.Clone(context);
                    _disposables.Add(cloned);
                    return cloned;
                }

                if (o is Document d)
                    return new BlittableObjectInstance(engine, Clone(d.Data), d.Id, d.LastModified);
                if (o is DocumentConflict dc)
                    return new BlittableObjectInstance(engine, Clone(dc.Doc), dc.Id, dc.LastModified);
                if (o is BlittableJsonReaderObject json)
                    return new BlittableObjectInstance(engine, json, null, null);
                // Removing this for now to see what breaks
                //if (o is BlittableJsonReaderArray array)
                //    return BlittableObjectInstance.CreateArrayInstanceBasedOnBlittableArray(engine, array);
                if (o == null)
                    return Null.Instance;
                if (o is long)
                    return o;
                if (o is List<object> l)
                {
                    var args = new[] { new JsValue(l.Count), };
                    var jsArray = ScriptEngine.Array.Construct(args);
                    for (int i = 0; i < l.Count; i++)
                    {
                        var value = TranslateToJs(ScriptEngine, context, l[i]);
                        args[0] = value as JsValue ?? JsValue.FromObject(ScriptEngine, value);
                        ScriptEngine.Array.PrototypeObject.Push(jsArray, args);
                    }
                    return jsArray;
                }
                // for admin
                if (o is RavenServer || o is DocumentDatabase)
                {
                    AssertAdminScriptInstance();
                    return o;
                }
                if (o is ObjectInstance)
                    return o;
#if DEBUG
                Debug.Assert(ExpectedTypes.Contains(o.GetType()));
#endif
                return o;
            }

            private void AssertAdminScriptInstance()
            {
                if (_runner._enableClr == false)
                    throw new InvalidOperationException("Unable to run admin scripts using this instance of the script runner, the EnableClr is set to false");
            }

            public object CreateEmptyObject()
            {
                return ScriptEngine.Object.Construct(Array.Empty<JsValue>());
            }

            internal static Action GetUselessOnStateLoopIterationInstanceForCodeGenerationOnly()
            {
                return new SingleRun().OnStateLoopIteration;
            }


            public object Translate(ScriptRunnerResult result, JsonOperationContext context, BlittableJsonDocumentBuilder.UsageMode usageMode = BlittableJsonDocumentBuilder.UsageMode.None)
            {
                var val = result.RawJsValue;
                if (val.IsString())
                    return val.AsString();
                if (val.IsBoolean())
                    return val.AsBoolean();
                if (val.IsObject())
                    return result.TranslateToObject(context, usageMode);
                if (val.IsNumber())
                    return val.AsNumber();
                if (val.IsNull() || val.IsUndefined())
                    return null;
                if (val.IsArray())
                    throw new InvalidOperationException("Returning arrays from scripts is not supported, only objects or primitves");
                throw new NotSupportedException("Unable to translate " + val.Type);
            }
        }

        public ScriptRunner(DocumentDatabase db, bool enableClr)
        {
            _db = db;
            _enableClr = enableClr;
        }

        private readonly ConcurrentQueue<SingleRun> _cache = new ConcurrentQueue<SingleRun>();

        public long Runs;

        public ReturnRun GetRunner(out SingleRun run)
        {
            if (_cache.TryDequeue(out run) == false)
            {
                run = new SingleRun(_db, this, ScriptsSource);
            }
            Interlocked.Increment(ref Runs);
            return new ReturnRun(this, run);
        }

        public struct ReturnRun : IDisposable
        {
            private ScriptRunner _parent;
            private SingleRun _run;

            public ReturnRun(ScriptRunner parent, SingleRun run)
            {
                _parent = parent;
                _run = run;
            }

            public void Dispose()
            {
                if (_run == null)
                    return;
                _run.ReadOnly = false;
                _run.DebugMode = false;
                _run.DebugOutput?.Clear();
                _run.DebugActions?.Clear();
                _parent._cache.Enqueue(_run);
                _run = null;
                _parent = null;
            }
        }

        public void TryCompileScript(string script)
        {
            try
            {
                var engine = new Jint.Engine(options =>
                {
                    options.MaxStatements(1).LimitRecursion(1);
                });
                engine.Execute(script);
            }
            catch (Exception e)
            {
                throw new JavaScriptParseException("Failed to parse:" + Environment.NewLine + script, e);
            }
        }
    }
}