﻿/// <reference path="../../../../typings/tsd.d.ts"/>
import ongoingTaskEditModel = require("models/database/tasks/ongoingTaskEditModel");
import ongoingTaskRavenEtlTransformationModel = require("models/database/tasks/ongoingTaskRavenEtlTransformationModel");
import jsonUtil = require("common/jsonUtil");

class ongoingTaskRavenEtlEditModel extends ongoingTaskEditModel {
    connectionStringName = ko.observable<string>();
    allowEtlOnNonEncryptedChannel = ko.observable<boolean>(false);
    transformationScripts = ko.observableArray<ongoingTaskRavenEtlTransformationModel>([]);

    showEditTransformationArea: KnockoutComputed<boolean>;

    editedTransformationScript = ko.observable<ongoingTaskRavenEtlTransformationModel>();  
    validationGroup: KnockoutValidationGroup;
    
    dirtyFlag: () => DirtyFlag;
    
    constructor(dto: Raven.Client.ServerWide.Operations.OngoingTaskRavenEtlDetails) {
        super();

        this.update(dto);
        this.initializeObservables();
        this.initValidation();
    }

    initializeObservables() {
        super.initializeObservables();
        
        this.showEditTransformationArea = ko.pureComputed(() => !!this.editedTransformationScript());
        
        const innerDirtyFlag = ko.pureComputed(() => this.editedTransformationScript() && this.editedTransformationScript().dirtyFlag().isDirty());
        
        this.dirtyFlag = new ko.DirtyFlag([innerDirtyFlag,
                this.taskName,
                this.preferredMentor,
                this.manualChooseMentor,
                this.connectionStringName,
                this.allowEtlOnNonEncryptedChannel,
                this.transformationScripts()
            ],
            false, jsonUtil.newLineNormalizingHashFunction);
    }
    
    private initValidation() {
        this.initializeMentorValidation();

        this.connectionStringName.extend({
            required: true
        });
        
        this.transformationScripts.extend({
            validation: [
                {
                    validator: () => this.transformationScripts().length > 0,
                    message: "Transformation Script is Not defined"
                }
            ]
        });

        this.validationGroup = ko.validatedObservable({
            connectionStringName: this.connectionStringName,
            preferredMentor: this.preferredMentor,
            transformationScripts: this.transformationScripts
        });
    }

    update(dto: Raven.Client.ServerWide.Operations.OngoingTaskRavenEtlDetails) {
        super.update(dto);

        if (dto.Configuration) {
            this.connectionStringName(dto.Configuration.ConnectionStringName);
            this.transformationScripts(dto.Configuration.Transforms.map(x => new ongoingTaskRavenEtlTransformationModel(x, false)));
            this.manualChooseMentor(!!dto.Configuration.MentorNode);
            this.preferredMentor(dto.Configuration.MentorNode);
        }
    }

    toDto(): Raven.Client.ServerWide.ETL.RavenEtlConfiguration { 
        return {
            Name: this.taskName(),
            ConnectionStringName: this.connectionStringName(),
            AllowEtlOnNonEncryptedChannel: this.allowEtlOnNonEncryptedChannel(),
            Disabled: false,
            Transforms: this.transformationScripts().map(x => x.toDto()),
            EtlType: "Raven",
            MentorNode: this.manualChooseMentor() ? this.preferredMentor() : undefined,
            TaskId: this.taskId,
        } as Raven.Client.ServerWide.ETL.RavenEtlConfiguration;
    }

    deleteTransformationScript(transformationScript: ongoingTaskRavenEtlTransformationModel) { 
        this.transformationScripts.remove(x => transformationScript.name() === x.name());
        
        if (this.editedTransformationScript() && this.editedTransformationScript().name() === transformationScript.name()) {
            this.editedTransformationScript(null);
        }
    }

    editTransformationScript(transformationScript: ongoingTaskRavenEtlTransformationModel) {
        this.editedTransformationScript(new ongoingTaskRavenEtlTransformationModel(transformationScript.toDto(), false));
        this.dirtyFlag().reset();
    }

    static empty(): ongoingTaskRavenEtlEditModel {
        return new ongoingTaskRavenEtlEditModel(
            {
                TaskName: "",
                TaskType: "RavenEtl",
                TaskState: "Enabled",
                TaskConnectionStatus: "Active",
                Configuration: {
                    EtlType: "Raven",
                    Transforms: [],
                    ConnectionStringName: "",
                    Name: "",
                },
            } as Raven.Client.ServerWide.Operations.OngoingTaskRavenEtlDetails);
    }
}

export = ongoingTaskRavenEtlEditModel;