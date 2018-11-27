/*
 * MindTouch λ#
 * Copyright (C) 2018 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using MindTouch.LambdaSharp.Tool.Internal;

namespace MindTouch.LambdaSharp.Tool.Model {

    public class ModuleBuilder : AModelProcessor {

        //--- Fields ---
        private readonly Module _module;

        //--- Constructors ---
        public ModuleBuilder(Settings settings, string sourceFilename, Module module) : base(settings, sourceFilename) {
            _module = module ?? throw new ArgumentNullException(nameof(module));
        }

        //--- Properties ---
        public VersionInfo Version => _module.Version;
        public bool HasPragma(string pragma) => _module.HasPragma(pragma);

        //--- Methods ---
        public Module ToModule() => _module;
        public AModuleEntry GetEntry(string fullName) =>  _module.GetEntry(fullName);
        public void AddCondition(string name, object condition) => _module.Conditions.Add(name, condition);
        public void AddPragma(object pragma) => _module.Pragmas.Add(pragma);

        public bool AddSecret(object secret) {
            if(secret is string textSecret) {
                if(textSecret.StartsWith("arn:")) {

                    // decryption keys provided with their ARN can be added as is; no further steps required
                    _module.Secrets.Add(secret);
                    return true;
                }

                // assume key name is an alias and resolve it to its ARN
                try {
                    var response = Settings.KmsClient.DescribeKeyAsync(textSecret).Result;
                    _module.Secrets.Add(response.KeyMetadata.Arn);
                    return true;
                } catch(Exception e) {
                    AddError($"failed to resolve key alias: {textSecret}", e);
                    return false;
                }
            } else {
                _module.Secrets.Add(secret);
                return true;
            }
        }

        public AModuleEntry AddValue(AModuleEntry parent, string name, string description, object reference, object scope, bool isSecret) {
            return AddEntry(new ValueEntry(parent, name, description, reference, ConvertScope(scope), isSecret));
        }

        public AModuleEntry AddInput(
            string name,
            string description = null,
            string type = null,
            string section = null,
            string label = null,
            object scope = null,
            bool? noEcho = null,
            string defaultValue = null,
            string constraintDescription = null,
            string allowedPattern = null,
            IList<string> allowedValues = null,
            int? maxLength = null,
            int? maxValue = null,
            int? minLength = null,
            int? minValue = null,
            string awsType = null,
            object awsAllow = null,
            IDictionary<string, object> awsProperties = null,
            string arnAttribute = null
        ) {

            // create input parameter entry
            var result = AddEntry(new InputEntry(
                parent: null,
                name: name,
                description: description,
                reference: null,
                scope: ConvertScope(scope),
                section: section,
                label: label,
                isSecret: (type == "Secret"),
                parameter: new Humidifier.Parameter {
                    Type = ConvertInputType(type),
                    Description = description,
                    Default = defaultValue,
                    ConstraintDescription = constraintDescription,
                    AllowedPattern = allowedPattern,
                    AllowedValues = allowedValues?.ToList(),
                    MaxLength = maxLength,
                    MaxValue = maxValue,
                    MinLength = minLength,
                    MinValue = minValue,
                    NoEcho = noEcho
                }
            ));

            // check if a conditional managed resource is associated with the input parameter
            if((awsType == null) && (awsAllow == null)) {

                // nothing to do
            } else if(defaultValue == null) {

                // request input parameter resource grants
                AddGrant(result.LogicalId, awsType, FnRef(result.ResourceName), awsAllow);
            } else {

                // create conditional managed resource
                var condition = $"{result.LogicalId}Created";
                AddCondition(condition, FnEquals(FnRef(result.ResourceName), defaultValue));
                var instance = AddResource(
                    parent: result,
                    name: "Resource",
                    description: null,
                    scope: null,
                    awsType: awsType,
                    awsProperties: awsProperties,
                    awsArnAttribute: arnAttribute,
                    dependsOn: null,
                    condition: condition
                );

                // register input parameter reference
                result.Reference = FnIf(
                    condition,
                    instance.GetExportReference(),
                    FnRef(result.ResourceName)
                );

                // request input parameter or conditional managed resource grants
                AddGrant(instance.LogicalId, awsType, result.Reference, awsAllow);
            }
            return result;
        }

        public AModuleEntry AddImport(
            string import,
            string description = null,
            string type = null,
            string section = null,
            string label = null,
            object scope = null,
            bool? noEcho = null,
            string awsType = null,
            object awsAllow = null
        ) {
            var parts = import.Split("::", 2);
            var exportModule = parts[0];
            var exportName = parts[1];

            // find or create root module import collection node
            var rootParameter = _module.TryGetEntry(exportModule, out AModuleEntry existingEntry)
                ? existingEntry
                : AddValue(
                    parent: null,
                    name: exportModule,
                    description: $"{exportModule} cross-module references",
                    reference: "",
                    scope: null,
                    isSecret: false
                );

            // create import parameter entry
            var result = AddEntry(new InputEntry(
                parent: rootParameter,
                name: exportName,
                description: description,
                reference: null,
                scope: ConvertScope(scope),
                section: section,
                label: label,
                isSecret: (type == "Secret"),
                parameter: new Humidifier.Parameter {
                    Type = ConvertInputType(type),
                    Description = description,
                    Default = "$" + import,
                    ConstraintDescription = "must either be a cross-module import reference or a non-blank value",
                    AllowedPattern =  @"^.+$",
                    NoEcho = noEcho
                }
            ));

            // register import parameter reference
            var condition = $"{result.LogicalId}IsImport";
            AddCondition(condition, FnEquals(FnSelect("0", FnSplit("$", FnRef(result.ResourceName))), ""));
            result.Reference = FnIf(
                condition,
                FnImportValue(FnSub("${DeploymentPrefix}${Import}", new Dictionary<string, object> {
                    ["Import"] = FnSelect("1", FnSplit("$", FnRef(result.ResourceName)))
                })),
                FnRef(result.ResourceName)
            );

            // check if resource grants is associated with the import parameter
            if((awsType != null) || (awsAllow != null)) {

                // request input parameter or conditional managed resource grants
                AddGrant(result.LogicalId, awsType, result.Reference, awsAllow);
            }
            return result;
        }

        public void AddExport(string name, string description, object value) {
            _module.Outputs.Add(new ExportOutput {
                Name = name,
                Description = description,
                Value = value
            });
        }

        public void AddCustomResource(string customResourceName, string description, string handler) {
            _module.Outputs.Add(new CustomResourceHandlerOutput {
                CustomResourceName = customResourceName,
                Description = description,
                Handler = FnRef(handler)
            });
        }

        public void AddMacro(string macroName, string description, string handler) {

            // check if a root macros collection needs to be created
            if(!_module.TryGetEntry("Macros", out AModuleEntry macrosEntry)) {
                macrosEntry = AddValue(
                    parent: null,
                    name: "Macros",
                    description: "Macro definitions",
                    reference: "",
                    scope: null,
                    isSecret: false
                );
            }

            // add macro resource
            AddResource(
                parent: macrosEntry,
                name: macroName,
                description: description,
                scope: null,
                resource: new Humidifier.CustomResource("AWS::CloudFormation::Macro") {

                    // TODO (2018-10-30, bjorg): we may want to set 'LogGroupName' and 'LogRoleARN' as well
                    ["Name"] = FnSub("${DeploymentPrefix}" + macroName),
                    ["Description"] = description ?? "",
                    ["FunctionName"] = FnRef(handler)
                },
                resourceArnAttribute: null,
                dependsOn: null,
                condition: null
            );
        }

        public AModuleEntry AddResource(
            AModuleEntry parent,
            string name,
            string description,
            object scope,
            Humidifier.Resource resource,
            string resourceArnAttribute,
            IList<string> dependsOn,
            string condition
        ) {
            var result = new HumidifierEntry(
                parent: parent,
                name: name,
                description: description,
                reference: null,
                scope: ConvertScope(scope),
                resource: resource,
                resourceArnAttribute: resourceArnAttribute,
                dependsOn: dependsOn,
                condition: condition
            );
            return AddEntry(result);
        }

        public AModuleEntry AddResource(
            AModuleEntry parent,
            string name,
            string description,
            object scope,
            string awsType,
            IDictionary<string, object> awsProperties,
            string awsArnAttribute,
            IList<string> dependsOn,
            string condition
        ) {
            var result = new HumidifierEntry(
                parent: parent,
                name: name,
                description: description,
                reference: null,
                scope: ConvertScope(scope),
                resource: new Humidifier.CustomResource(awsType, awsProperties),
                resourceArnAttribute: awsArnAttribute,
                dependsOn: dependsOn,
                condition: condition
            );
            return AddEntry(result);
        }

        public AModuleEntry AddPackage(
            AModuleEntry parent,
            string name,
            string description,
            object scope,
            object destinationBucket,
            object destinationKeyPrefix,
            string sourceFilepath
        ) {
            var result = new PackageEntry(
                parent: parent,
                name: name,
                description: description,
                scope: ConvertScope(scope),
                sourceFilepath: sourceFilepath,
                destinationBucket: destinationBucket,
                destinationKeyPrefix: destinationKeyPrefix
            );
            result.Reference = FnGetAtt(result.ResourceName, "Url");
            return AddEntry(result);
        }

        public AModuleEntry AddFunction(
            AModuleEntry parent,
            string name,
            string description,
            string project,
            string language,
            IDictionary<string, object> environment,
            IList<AFunctionSource> sources,
            IList<object> pragmas,
            string timeout,
            string runtime,
            string reservedConcurrency,
            string memory,
            string handler,
            object subnets,
            object securityGroups
        ) {

            // initialize optional VPC configuration
            Humidifier.Lambda.FunctionTypes.VpcConfig vpc = null;
            if((subnets != null) && (securityGroups != null)) {
                vpc = new Humidifier.Lambda.FunctionTypes.VpcConfig {
                    SubnetIds = subnets,
                    SecurityGroupIds = securityGroups
                };
            }
            var result = new FunctionEntry(
                parent: parent,
                name: name,
                description: description,
                reference: null,
                scope: new string[0],
                project: project,
                language: language,
                environment: environment ?? new Dictionary<string, object>(),
                sources: sources ?? new AFunctionSource[0],
                pragmas: pragmas ?? new object[0],
                function: new Humidifier.Lambda.Function {
                    Description = (description != null)
                        ? description.TrimEnd() + $" (v{_module.Version})"
                        : null,
                    Timeout = timeout,
                    Runtime = runtime,
                    ReservedConcurrentExecutions = reservedConcurrency,
                    MemorySize = memory,
                    Handler = handler,
                    VpcConfig = vpc,
                    Role = FnGetAtt("Module::Role", "Arn"),
                    Environment = new Humidifier.Lambda.FunctionTypes.Environment {
                        Variables = new Dictionary<string, dynamic>()
                    }
                }
            );
            if(_module.HasLambdaSharpDependencies) {
                result.Function.DeadLetterConfig = new Humidifier.Lambda.FunctionTypes.DeadLetterConfig {
                    TargetArn = FnRef("Module::DeadLetterQueueArn")
                };
            }
            return AddEntry(result);
        }

        public void AddGrant(string sid, string awsType, object reference, object allow) {

            // resolve shorthands and deduplicate statements
            var allowStatements = new List<string>();
            foreach(var allowStatement in ConvertToStringList(allow)) {
                if(allowStatement == "None") {

                    // nothing to do
                } else if(allowStatement.Contains(':')) {

                    // AWS permission statements always contain a `:` (e.g `ssm:GetParameter`)
                    allowStatements.Add(allowStatement);
                } else if(ResourceMapping.TryResolveAllowShorthand(awsType, allowStatement, out IList<string> allowedList)) {
                    allowStatements.AddRange(allowedList);
                } else {
                    AddError($"could not find IAM mapping for short-hand '{allowStatement}' on AWS type '{awsType}'");
                }
            }
            if(!allowStatements.Any()) {
                return;
            }

            // add role resource statement
            _module.ResourceStatements.Add(new Humidifier.Statement {
                Sid = sid,
                Effect = "Allow",
                Resource = ResourceMapping.ExpandResourceReference(awsType, reference),
                Action = allowStatements.Distinct().OrderBy(text => text).ToList()
            });
        }

        private IList<string> ConvertScope(object scope) {
            if(scope == null) {
                return new string[0];
            }
            return AtLocation("Scope", () => {
                return (scope == null)
                    ? new List<string>()
                    : ConvertToStringList(scope);
            });
        }

        private AModuleEntry AddEntry(AModuleEntry entry) {

            // set default reference
            if(entry.Reference == null) {
                entry.Reference = FnRef(entry.ResourceName);
            }

            // add entry
            return _module.AddEntry(entry);
        }

        private string ConvertInputType(string type)
            => ((type == null) || (type == "Secret"))
                ? "String"
                : type;
    }
}