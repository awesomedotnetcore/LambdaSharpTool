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
using System.Text.RegularExpressions;
using MindTouch.LambdaSharp.Tool.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MindTouch.LambdaSharp.Tool.Model {
    using static ModelFunctions;

    public class ModuleBuilder : AModelProcessor {

        //--- Fields ---
        private readonly string _name;
        private readonly VersionInfo _version;
        private readonly string _description;
        private IList<object> _pragmas;
        private IList<object> _secrets;
        private IDictionary<string, AModuleEntry> _entriesByFullName;
        private IList<AModuleEntry> _entries;
        private IDictionary<string, object> _conditions;
        private IList<AOutput> _outputs;
        private IList<Humidifier.Statement> _resourceStatements = new List<Humidifier.Statement>();
        private IList<string> _assets;

        //--- Constructors ---
        public ModuleBuilder(Settings settings, string sourceFilename, Module module) : base(settings, sourceFilename) {
            _name = module.Name;
            _version = module.Version;
            _description = module.Description;
            _pragmas = new List<object>(module.Pragmas ?? new object[0]);
            _secrets = new List<object>(module.Secrets ?? new object[0]);
            _entries = new List<AModuleEntry>(module.Entries ?? new AModuleEntry[0]);
            _entriesByFullName = _entries.ToDictionary(entry => entry.FullName);
            _conditions = new Dictionary<string, object>(module.Conditions ?? new KeyValuePair<string, object>[0]);
            _outputs = new List<AOutput>(module.Outputs ?? new AOutput[0]);
            _assets = new List<string>(module.Assets ?? new string[0]);

            // extract existing resource statements when they exist
            if(TryGetEntry("Module::Role", out AModuleEntry moduleRoleEntry)) {
                var role = (Humidifier.IAM.Role)((ResourceEntry)moduleRoleEntry).Resource;
                _resourceStatements = new List<Humidifier.Statement>(role.Policies[0].PolicyDocument.Statement);
                role.Policies[0].PolicyDocument.Statement = new List<Humidifier.Statement>();
            } else {
                _resourceStatements = new List<Humidifier.Statement>();
            }
        }

        //--- Properties ---
        public string Name => _name;
        public VersionInfo Version => _version;
        public IEnumerable<object> Secrets => _secrets;
        public IEnumerable<AModuleEntry> Entries => _entries;
        public IEnumerable<AOutput> Outputs => _outputs;
        public bool HasPragma(string pragma) => _pragmas.Contains(pragma);
        public bool HasModuleRegistration => !HasPragma("no-module-registration");
        public bool HasLambdaSharpDependencies => !HasPragma("no-lambdasharp-dependencies");

        //--- Methods ---
        public AModuleEntry GetEntry(string fullName) => _entriesByFullName[fullName];
        public void AddCondition(string name, object condition) => _conditions.Add(name, condition);
        public void AddPragma(object pragma) => _pragmas.Add(pragma);
        public bool TryGetEntry(string fullName, out AModuleEntry entry) => _entriesByFullName.TryGetValue(fullName, out entry);
        public void AddAsset(string asset) => _assets.Add(asset);

        public bool AddSecret(object secret) {
            if(secret is string textSecret) {
                if(textSecret.StartsWith("arn:")) {

                    // decryption keys provided with their ARN can be added as is; no further steps required
                    _secrets.Add(secret);
                    return true;
                }

                // assume key name is an alias and resolve it to its ARN
                try {
                    var response = Settings.KmsClient.DescribeKeyAsync(textSecret).Result;
                    _secrets.Add(response.KeyMetadata.Arn);
                    return true;
                } catch(Exception e) {
                    AddError($"failed to resolve key alias: {textSecret}", e);
                    return false;
                }
            } else {
                _secrets.Add(secret);
                return true;
            }
        }

        public AModuleEntry AddParameter(
            string name,
            string section,
            string label,
            string description,
            string type,
            IList<string> scope,
            bool? noEcho,
            string defaultValue,
            string constraintDescription,
            string allowedPattern,
            IList<string> allowedValues,
            int? maxLength,
            int? maxValue,
            int? minLength,
            int? minValue,
            object allow,
            IDictionary<string, object> properties,
            string arnAttribute,
            IDictionary<string, string> encryptionContext
        ) {

            // create input parameter entry
            var result = AddEntry(new InputEntry(
                parent: null,
                name: name,
                section: section,
                label: label,
                description: description,
                type: type,
                scope: scope,
                reference: null,
                parameter: new Humidifier.Parameter {
                    Type = ResourceMapping.ToCloudFormationType(type),
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
            if(!result.HasAwsType) {

                // nothing to do
            } else if(defaultValue == null) {

                // request input parameter resource grants
                AddGrant(result.LogicalId, type, FnRef(result.ResourceName), allow);
            } else {

                // create conditional managed resource
                var condition = $"{result.LogicalId}Created";
                AddCondition(condition, FnEquals(FnRef(result.ResourceName), defaultValue));
                var instance = AddResource(
                    parent: result,
                    name: "Resource",
                    description: null,
                    scope: null,
                    value: null,
                    type: type,
                    allow: null,
                    properties: properties,
                    arnAttribute: arnAttribute,
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
                AddGrant(instance.LogicalId, type, result.Reference, allow);
            }
            return result;
        }

        public AModuleEntry AddImport(
            string import,
            string section,
            string label,
            string description,
            string type,
            object scope,
            bool? noEcho,
            object allow
        ) {
            var parts = import.Split("::", 2);
            var exportModule = parts[0];
            var exportName = parts[1];

            // find or create root module import collection node
            var rootParameter = TryGetEntry(exportModule, out AModuleEntry existingEntry)
                ? existingEntry
                : AddVariable(
                    parent: null,
                    name: exportModule,
                    description: $"{exportModule} cross-module references",
                    type: "String",
                    scope: null,
                    value: "",
                    encryptionContext: null
                );

            // create import parameter entry
            var result = AddEntry(new InputEntry(
                parent: rootParameter,
                name: exportName,
                section: section,
                label: label,
                description: description,
                scope: ConvertScope(scope),
                type: type,
                reference: null,
                parameter: new Humidifier.Parameter {
                    Type = ResourceMapping.ToCloudFormationType(type),
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
            if(!result.HasAwsType) {

                // nothing to do
            } else {

                // request import resource grants
                AddGrant(result.LogicalId, type, result.GetExportReference(), allow);
            }
            return result;
        }

        public void AddExport(string name, string description, object value) {
            _outputs.Add(new ExportOutput {
                Name = name,
                Description = description,
                Value = value
            });
        }

        public void AddCustomResource(string customResourceName, string description, string handler) {
            _outputs.Add(new CustomResourceHandlerOutput {
                CustomResourceName = customResourceName,
                Description = description,
                Handler = FnRef(handler)
            });
        }

        public void AddMacro(string macroName, string description, string handler) {

            // check if a root macros collection needs to be created
            if(!TryGetEntry("Macros", out AModuleEntry macrosEntry)) {
                macrosEntry = AddVariable(
                    parent: null,
                    name: "Macros",
                    description: "Macro definitions",
                    type: "String",
                    scope: null,
                    value: "",
                    encryptionContext: null
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

        public AModuleEntry AddVariable(
            AModuleEntry parent,
            string name,
            string description,
            string type,
            IList<string> scope,
            object value,
            IDictionary<string, string> encryptionContext
        ) {
            if(value == null) {
                throw new ArgumentNullException(nameof(value));
            }

            // the format for secrets with encryption keys is: SECRET|KEY1=VALUE1|KEY2=VALUE2
            object reference;
            if(encryptionContext != null) {
                reference = FnJoin(
                    "|",
                    new object[] {
                        value
                    }.Union(encryptionContext
                        ?.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")
                        ?? new string[0]
                    ).ToArray()
                );
            } else {
                reference = (value is IList<object> values)
                    ? FnJoin(",", values)
                    : value;
            }
            return AddEntry(new VariableEntry(parent, name, description, type, scope, reference));
        }

        public AModuleEntry AddResource(
            AModuleEntry parent,
            string name,
            string description,
            IList<string> scope,
            Humidifier.Resource resource,
            string resourceArnAttribute,
            IList<string> dependsOn,
            string condition
        ) {
            var result = new ResourceEntry(
                parent: parent,
                name: name,
                description: description,
                scope: scope,
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
            string type,
            IList<string> scope,
            object value,
            object allow,
            IDictionary<string, object> properties,
            IList<string> dependsOn,
            string arnAttribute,
            string condition
        ) {

            // check if a referenced or managed resource should be created
            AModuleEntry result;
            if(value != null) {

                // add variable to hold the referenced resource
                result = AddVariable(
                    parent: parent,
                    name: name,
                    description: description,
                    type: type,
                    scope: scope,
                    value: value,
                    encryptionContext: null
                );

                // add optional grants
                if(allow != null) {
                    AddGrant(result.LogicalId, type, value, allow);
                }
            } else {

                // validate resource properties
                if(type.StartsWith("AWS::", StringComparison.Ordinal)) {
                    var awsType = ResourceMapping.GetHumidifierType(type);
                    if((awsType != null) && (properties != null)) {
                        try {

                            // TODO (2018-12-01, bjorg): use CloudFormation JSON spec for this

                            // validate fields
                            JObject.FromObject(properties)
                                .ToObject(awsType, new JsonSerializer {
                                    MissingMemberHandling = MissingMemberHandling.Error
                                });
                        } catch(JsonSerializationException e) {
                            AddError($"{e.Message} [Resource Type: {awsType}]");
                        }
                    }
                }

                // create resource entry
                result = AddResource(
                    parent: parent,
                    name: name,
                    description: description,
                    scope: scope,
                    resource: new Humidifier.CustomResource(type, properties),
                    resourceArnAttribute: arnAttribute,
                    dependsOn: dependsOn,
                    condition: condition
                );

                // add optional grants
                if(allow != null) {
                    AddGrant(result.LogicalId, type, result.GetExportReference(), allow);
                }
            }
            return result;
        }

        public AModuleEntry AddModule(
            AModuleEntry parent,
            string name,
            string description,
            object module,
            object version,
            object sourceBucketName,
            IList<string> scope,
            object dependsOn,
            IDictionary<string, object> parameters
        ) {
            var source = sourceBucketName ?? FnRef("DeploymentBucketName");
            var moduleParameters = parameters.ToDictionary(kv => kv.Key, kv => kv.Value);
            AtLocation("Parameters", () => {
                OptionalAdd("LambdaSharpDeadLetterQueueArn", FnRef("Module::DeadLetterQueueArn"));
                OptionalAdd("LambdaSharpLoggingStreamArn", FnRef("Module::LoggingStreamArn"));
                OptionalAdd("LambdaSharpDefaultSecretKeyArn", FnRef("Module::DefaultSecretKeyArn"));
                MandatoryAdd("DeploymentBucketName", source);
                MandatoryAdd("DeploymentPrefix", FnRef("DeploymentPrefix"));
                MandatoryAdd("DeploymentPrefixLowercase", FnRef("DeploymentPrefixLowercase"));
                MandatoryAdd("DeploymentParent", FnRef("AWS::StackName"));
            });

            // add stack resource
            return AddResource(
                parent: parent,
                name: name,
                description: description,
                scope: scope,
                resource: new Humidifier.CloudFormation.Stack {
                    NotificationARNs = FnRef("AWS::NotificationARNs"),
                    Parameters = moduleParameters,
                    TemplateURL = FnSub("https://${ModuleSourceBucketName}.s3.${AWS::Region}.amazonaws.com/Modules/${ModuleName}/Versions/${ModuleVersion}/cloudformation.json", new Dictionary<string, object> {
                        ["ModuleSourceBucketName"] = source,
                        ["ModuleName"] = module,
                        ["ModuleVersion"] = version
                    }),

                    // TODO (2018-11-29, bjorg): make timeout configurable
                    TimeoutInMinutes = 5
                },
                resourceArnAttribute: null,
                dependsOn: ConvertToStringList(dependsOn),
                condition: null
            );

            // local function
            void OptionalAdd(string key, object value) {
                if(!moduleParameters.ContainsKey(key)) {
                    moduleParameters.Add(key, value);
                }
            }

            void MandatoryAdd(string key, object value) {
                if(!moduleParameters.ContainsKey(key)) {
                    moduleParameters.Add(key, value);
                } else {
                    AddError($"'{key}' is a reserved attribute and cannot be specified");
                }
            }
        }

        public AModuleEntry AddPackage(
            AModuleEntry parent,
            string name,
            string description,
            object scope,
            string files
        ) {
            var result = new PackageEntry(
                parent: parent,
                name: name,
                description: description,
                scope: ConvertScope(scope),
                sourceFilepath: files
            );
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
                scope: new string[0],
                project: project,
                language: language,
                environment: environment ?? new Dictionary<string, object>(),
                sources: sources ?? new AFunctionSource[0],
                pragmas: pragmas ?? new object[0],
                function: new Humidifier.Lambda.Function {
                    Description = (description != null)
                        ? description.TrimEnd() + $" (v{_version})"
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
            if(HasLambdaSharpDependencies) {
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
                } else if((awsType != null) && ResourceMapping.TryResolveAllowShorthand(awsType, allowStatement, out IList<string> allowedList)) {
                    allowStatements.AddRange(allowedList);
                } else {
                    AddError($"could not find IAM mapping for short-hand '{allowStatement}' on AWS type '{awsType ?? "<omitted>"}'");
                }
            }
            if(!allowStatements.Any()) {
                return;
            }

            // add role resource statement
            var statement = new Humidifier.Statement {
                Sid = sid,
                Effect = "Allow",
                Resource = ResourceMapping.ExpandResourceReference(awsType, reference),
                Action = allowStatements.Distinct().OrderBy(text => text).ToList()
            };
            for(var i = 0; i < _resourceStatements.Count; ++i) {
                if(_resourceStatements[i].Sid == sid) {
                    _resourceStatements[i] = statement;
                    return;
                }
            }
            _resourceStatements.Add(statement);
        }

        public void VisitAll(Func<object, object> visitor) {
            if(visitor == null) {
                throw new ArgumentNullException(nameof(visitor));
            }

            // resolve references in secrets
            AtLocation("Secrets", () => {
                _secrets = (IList<object>)visitor(_secrets);
            });

            // resolve references in entries
            AtLocation("Entries", () => {
                foreach(var entry in _entries) {
                    AtLocation(entry.FullName, () => {
                        switch(entry) {
                        case InputEntry _:
                        case VariableEntry _:

                            // nothing to do
                            break;
                        case PackageEntry package:
                            AtLocation("Package", () => {

                                // TODO: fix this
                                // package.Package = (Humidifier.CustomResource)visitor(package.Package);
                            });
                            break;
                        case ResourceEntry humidifier:
                            AtLocation("Resource", () => {
                                humidifier.Resource = (Humidifier.Resource)visitor(humidifier.Resource);
                            });
                            AtLocation("DependsOn", () => {

                                // TODO (2018-11-29, bjorg): we need to make sure that only other resources are referenced (no literal entries, or itself, no loops either)
                                humidifier.DependsOn = humidifier.DependsOn.Select(dependency => {
                                    TryGetFnRef(visitor(FnRef(dependency)), out string result);
                                    return result ?? throw new InvalidOperationException("invalid expression returned");
                                }).ToList();
                            });
                            break;
                        case FunctionEntry function:
                            AtLocation("Environment", () => {
                                function.Environment = (IDictionary<string, object>)visitor(function.Environment);
                            });
                            AtLocation("Function", () => {
                                function.Function = (Humidifier.Lambda.Function)visitor(function.Function);
                            });

                            // update function sources
                            AtLocation("Sources", () => {
                                var index = 0;
                                foreach(var source in function.Sources) {
                                    AtLocation($"[{++index}]", () => {
                                        switch(source) {
                                        case AlexaSource alexaSource:
                                            if(alexaSource.EventSourceToken != null) {
                                                alexaSource.EventSourceToken = visitor(alexaSource.EventSourceToken);
                                            }
                                            break;
                                        }
                                    });
                                }
                            });
                            break;
                        default:
                            throw new ApplicationException($"unexpected type: {entry.GetType()}");
                        }
                    });
                }
            });

            // resolve references in conditions
            AtLocation("Conditions", () => {
                _conditions = (IDictionary<string, object>)visitor(_conditions);
            });

            // resolve references in output values
            AtLocation("Outputs", () => {
                foreach(var output in _outputs) {
                    switch(output) {
                    case ExportOutput exportOutput:
                        AtLocation(exportOutput.Name, () => {
                            AtLocation("Value", () => {
                                exportOutput.Value = visitor(exportOutput.Value);
                            });
                        });
                        break;
                    case CustomResourceHandlerOutput customResourceHandlerOutput:
                        AtLocation(customResourceHandlerOutput.CustomResourceName, () => {
                            AtLocation("Handler", () => {
                                customResourceHandlerOutput.Handler = visitor(customResourceHandlerOutput.Handler);
                            });
                        });
                        break;
                    default:
                        throw new InvalidOperationException($"cannot resolve references for this type: {output?.GetType()}");
                    }
                }
            });

            // resolve references in output values
            AtLocation("ResourceStatements", () => {
                _resourceStatements = (IList<Humidifier.Statement>)visitor(_resourceStatements);
            });
        }

        public Module ToModule() {

            // update existing resource statements when they exist
            if(TryGetEntry("Module::Role", out AModuleEntry moduleRoleEntry)) {
                var role = (Humidifier.IAM.Role)((ResourceEntry)moduleRoleEntry).Resource;
                role.Policies[0].PolicyDocument.Statement = _resourceStatements.ToList();
            }
            return new Module {
                Name = _name,
                Version = _version,
                Description = _description,
                Pragmas = _pragmas,
                Secrets = _secrets,
                Outputs = _outputs,
                Conditions = _conditions,
                Entries = _entries,
                Assets = _assets
            };
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
            Validate(Regex.IsMatch(entry.Name, CLOUDFORMATION_ID_PATTERN), "name is not valid");

            // set default reference
            if(entry.Reference == null) {
                entry.Reference = FnRef(entry.ResourceName);
            }

            // add entry
            if(_entriesByFullName.TryAdd(entry.FullName, entry)) {
                _entries.Add(entry);
            } else {
                AddError($"duplicate name '{entry.FullName}'");
            }
            return entry;
        }
    }
}