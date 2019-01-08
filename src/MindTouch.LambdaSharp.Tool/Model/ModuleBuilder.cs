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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MindTouch.LambdaSharp.Tool.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MindTouch.LambdaSharp.Tool.Model {
    using static ModelFunctions;

    public class ModuleBuilder : AModelProcessor {

        //--- Fields ---
        public readonly string _owner;
        private readonly string _name;
        private readonly VersionInfo _version;
        private readonly string _description;
        private IList<object> _pragmas;
        private IList<object> _secrets;
        private Dictionary<string, AModuleItem> _itemsByFullName;
        private List<AModuleItem> _items;
        private IList<Humidifier.Statement> _resourceStatements = new List<Humidifier.Statement>();
        private IList<string> _assets;
        private IDictionary<string, ModuleDependency> _dependencies;
        private IDictionary<string, ModuleManifestCustomResource> _customResourceTypes;
        private IList<string> _macroNames;
        private IDictionary<string, string> _resourceTypeNameMappings;

        //--- Constructors ---
        public ModuleBuilder(Settings settings, string sourceFilename, Module module) : base(settings, sourceFilename) {
            _owner = module.Owner;
            _name = module.Name;
            _version = module.Version;
            _description = module.Description;
            _pragmas = new List<object>(module.Pragmas ?? new object[0]);
            _secrets = new List<object>(module.Secrets ?? new object[0]);
            _items = new List<AModuleItem>(module.Items ?? new AModuleItem[0]);
            _itemsByFullName = _items.ToDictionary(item => item.FullName);
            _assets = new List<string>(module.Assets ?? new string[0]);
            _dependencies = (module.Dependencies != null)
                ? new Dictionary<string, ModuleDependency>(module.Dependencies)
                : new Dictionary<string, ModuleDependency>();
            _customResourceTypes = (module.CustomResourceTypes != null)
                ? new Dictionary<string, ModuleManifestCustomResource>(module.CustomResourceTypes)
                : new Dictionary<string, ModuleManifestCustomResource>();
            _macroNames = new List<string>(module.MacroNames ?? new string[0]);
            _resourceTypeNameMappings = module.ResourceTypeNameMappings ?? new Dictionary<string, string>();

            // extract existing resource statements when they exist
            if(TryGetItem("Module::Role", out AModuleItem moduleRoleItem)) {
                var role = (Humidifier.IAM.Role)((ResourceItem)moduleRoleItem).Resource;
                _resourceStatements = new List<Humidifier.Statement>(role.Policies[0].PolicyDocument.Statement);
                role.Policies[0].PolicyDocument.Statement = new List<Humidifier.Statement>();
            } else {
                _resourceStatements = new List<Humidifier.Statement>();
            }
        }

        //--- Properties ---
        public string Owner => _owner;
        public string Name => _name;
        public string FullName => $"{_owner}.{_name}";
        public string Info => $"{FullName}:{Version}";
        public VersionInfo Version => _version;
        public IEnumerable<object> Secrets => _secrets;
        public IEnumerable<AModuleItem> Items => _items;
        public IEnumerable<Humidifier.Statement> ResourceStatements => _resourceStatements;
        public bool HasPragma(string pragma) => _pragmas.Contains(pragma);
        public bool HasModuleRegistration => !HasPragma("no-module-registration");
        public bool HasLambdaSharpDependencies => !HasPragma("no-lambdasharp-dependencies");

        public bool TryGetLabeledPragma(string key, out object value) {
            foreach(var dictionaryPragma in _pragmas.OfType<IDictionary>()) {
                var entry = dictionaryPragma[key];
                if(entry != null) {
                    value = entry;
                    return true;
                }
            }
            value = null;
            return false;
        }

        //--- Methods ---
        public AModuleItem GetItem(string fullNameOrResourceName) {
            if(fullNameOrResourceName.StartsWith("@", StringComparison.Ordinal)) {
                return _items.FirstOrDefault(e => e.ResourceName == fullNameOrResourceName) ?? throw new KeyNotFoundException(fullNameOrResourceName);
            }
            return _itemsByFullName[fullNameOrResourceName];
        }

        public void AddPragma(object pragma) => _pragmas.Add(pragma);

        public bool TryGetItem(string fullNameOrResourceName, out AModuleItem item) {
            if(fullNameOrResourceName == null) {
                item = null;
                return false;
            }
            if(fullNameOrResourceName.StartsWith("@", StringComparison.Ordinal)) {
                item = _items.FirstOrDefault(e => e.ResourceName == fullNameOrResourceName);
                return item != null;
            }
            return _itemsByFullName.TryGetValue(fullNameOrResourceName, out item);
        }

        public void RemoveItem(string fullName) {
            if(TryGetItem(fullName, out AModuleItem item)) {

                // check if the module role is being removed
                if(fullName == "Module::Role") {

                    // remove all resource statements
                    _resourceStatements.Clear();

                    // remove all secrets
                    _secrets.Clear();
                }
                _items.Remove(item);
                _itemsByFullName.Remove(item.FullName);
            }
        }

        public void AddAsset(string fullName, string asset) {
            _assets.Add(Path.GetRelativePath(Settings.OutputDirectory, asset));

            // update item with the name of the asset
            GetItem(fullName).Reference = Path.GetFileName(asset);
        }

        public void AddDependency(string moduleFullName, VersionInfo minVersion, VersionInfo maxVersion, string bucketName) {

            // check if a dependency was already registered
            ModuleDependency dependency;
            if(_dependencies.TryGetValue(moduleFullName, out dependency)) {

                // keep the strongest version constraints
                if(minVersion != null) {
                    if((dependency.MinVersion == null) || (dependency.MinVersion < minVersion)) {
                        dependency.MinVersion = minVersion;
                    }
                }
                if(maxVersion != null) {
                    if((dependency.MaxVersion == null) || (dependency.MaxVersion > maxVersion)) {
                        dependency.MaxVersion = maxVersion;
                    }
                }

                // check there is no conflict in origin bucket names
                if(bucketName != null) {
                    if(dependency.BucketName == null) {
                        dependency.BucketName = bucketName;
                    }
                    if(dependency.BucketName != bucketName) {
                        AddError($"module {moduleFullName} source bucket conflict is empty ({dependency.BucketName} vs. {bucketName})");
                    }
                }
            } else {
                dependency = new ModuleDependency {
                    ModuleFullName = moduleFullName,
                    MinVersion = minVersion,
                    MaxVersion = maxVersion,
                    BucketName = bucketName
                };
            }

            // validate dependency
            if((dependency.MinVersion != null) && (dependency.MaxVersion != null) && (dependency.MinVersion > dependency.MaxVersion)) {
                AddError($"module {moduleFullName} version range is empty (v{dependency.MinVersion}..v{dependency.MaxVersion})");
                return;
            }
            if(!Settings.NoDependencyValidation) {
                if(!moduleFullName.TryParseModuleOwnerName(out string moduleOwner, out string moduleName)) {
                    AddError("invalid module reference");
                    return;
                }
                var loader = new ModelManifestLoader(Settings, moduleFullName);
                var location = loader.LocateAsync(moduleOwner, moduleName, minVersion, maxVersion, bucketName).Result;
                if(location == null) {
                    return;
                }
                var manifest = new ModelManifestLoader(Settings, moduleFullName).LoadFromS3Async(location.ModuleBucketName, location.TemplatePath).Result;
                if(manifest == null) {

                    // nothing to do; loader already emitted an error
                    return;
                }

                // update manifest in dependency
                dependency.Manifest = manifest;
            }
            if(!_dependencies.ContainsKey(moduleFullName)) {
                _dependencies.Add(moduleFullName, dependency);
            }
        }

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

        public AModuleItem AddParameter(
            AModuleItem parent,
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
            IDictionary<string, string> encryptionContext,
            IList<object> pragmas
        ) {

            // create input parameter item
            var parameter = new Humidifier.Parameter {
                Type = ResourceMapping.ToCloudFormationParameterType(type),
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
            };
            var result = AddItem(new ParameterItem(
                parent: parent,
                name: name,
                section: section ?? parent?.Description,
                label: label,
                description: description,
                type: type,
                scope: scope,
                reference: null,
                parameter: parameter
            ));

            // check if parameter belongs to an import statement
            if(parent != null) {

                // default value for an imported parameter is always the cross-module reference
                parameter.Default = "$" + result.FullName;

                // set default settings for import parameters
                if(constraintDescription == null) {
                    parameter.ConstraintDescription = "must either be a cross-module import reference or a non-empty value";
                }
                if(allowedPattern == null) {
                    parameter.AllowedPattern =  @"^.+$";
                }

                // register import parameter reference
                var condition = AddCondition(
                    parent: result,
                    name: "IsImported",
                    description: null,
                    value: FnAnd(
                        FnNot(FnEquals(FnRef(result.ResourceName), "")),
                        FnEquals(FnSelect("0", FnSplit("$", FnRef(result.ResourceName))), "")
                    )
                );
                result.Reference = FnIf(
                    condition.ResourceName,
                    FnImportValue(FnSub("${DeploymentPrefix}${Import}", new Dictionary<string, object> {
                        ["Import"] = FnSelect("1", FnSplit("$", FnRef(result.ResourceName)))
                    })),
                    FnRef(result.ResourceName)
                );
            }

            // check if a resource-type is associated with the input parameter
            if(result.HasSecretType) {
                var decoder = AddResource(
                    parent: result,
                    name: "Plaintext",
                    description: null,
                    scope: null,
                    resource: CreateDecryptSecretResourceFor(result),
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: null,
                    pragmas: null
                );
                decoder.Reference = FnGetAtt(decoder.ResourceName, "Plaintext");
                decoder.DiscardIfNotReachable = true;
            } else if(!result.HasAwsType) {

                // nothing to do
            } else if(defaultValue == null) {

                // request input parameter resource grants
                AddGrant(result.LogicalId, type, result.Reference, allow);
            } else {

                // create conditional managed resource
                var condition = AddCondition(
                    parent: result,
                    name: "IsCreated",
                    description: null,
                    value: FnEquals(FnRef(result.ResourceName), defaultValue)
                );
                var instance = AddResource(
                    parent: result,
                    name: "Resource",
                    description: null,
                    scope: null,
                    type: type,
                    allow: null,
                    properties: properties,
                    arnAttribute: arnAttribute,
                    dependsOn: null,
                    condition: condition.ResourceName,
                    pragmas: pragmas
                );

                // register input parameter reference
                result.Reference = FnIf(
                    condition.ResourceName,
                    instance.GetExportReference(),
                    result.Reference
                );

                // request input parameter or conditional managed resource grants
                AddGrant(instance.LogicalId, type, result.Reference, allow);
            }
            return result;
        }

        public AModuleItem AddUsing(
            string import,
            string description
        ) {
            var result = AddVariable(
                parent: null,
                name: import,
                description: description ?? $"{import} cross-module references",
                type: "String",
                scope: null,
                value: "",
                allow: null,
                encryptionContext: null
            );
            return result;
        }

        public void AddResourceType(
            string customResourceType,
            string description,
            string handler,
            ModuleManifestCustomResource properties
        ) {

            // TODO (2018-09-20, bjorg): add custom resource name validation
            AddItem(new ResourceTypeItem(customResourceType, description, FnRef(handler)));
            _customResourceTypes.Add(customResourceType, properties ?? new ModuleManifestCustomResource());
        }

        public AModuleItem AddMacro(string macroName, string description, string handler) {
            Validate(Regex.IsMatch(macroName, CLOUDFORMATION_ID_PATTERN), "name is not valid");

            // check if a root macros collection needs to be created
            if(!TryGetItem("Macros", out AModuleItem macrosItem)) {
                macrosItem = AddVariable(
                    parent: null,
                    name: "Macros",
                    description: "Macro definitions",
                    type: "String",
                    scope: null,
                    value: "",
                    allow: null,
                    encryptionContext: null
                );
            }

            // add macro resource
            var result = AddResource(
                parent: macrosItem,
                name: macroName,
                description: description,
                scope: null,
                resource: new Humidifier.CloudFormation.Macro {

                    // TODO (2018-10-30, bjorg): we may want to set 'LogGroupName' and 'LogRoleARN' as well
                    Name = FnSub("${DeploymentPrefix}" + macroName),
                    Description = description ?? "",
                    FunctionName = FnRef(handler)
                },
                resourceArnAttribute: null,
                dependsOn: null,
                condition: null,
                pragmas: null
            );
            _macroNames.Add(macroName);
            return result;
        }

        public AModuleItem AddVariable(
            AModuleItem parent,
            string name,
            string description,
            string type,
            IList<string> scope,
            object value,
            object allow,
            IDictionary<string, string> encryptionContext
        ) {
            if(value == null) {
                throw new ArgumentNullException(nameof(value));
            }
            var result = AddItem(new VariableItem(parent, name, description, type, scope, reference: null));

            // the format for secrets with encryption keys is: SECRET|KEY1=VALUE1|KEY2=VALUE2
            if(encryptionContext != null) {
                Validate(type == "Secret", "type must be 'Secret' to use 'EncryptionContext'");
                result.Reference = FnJoin(
                    "|",
                    new object[] {
                        value
                    }.Union(
                        encryptionContext.Select(kv => $"{kv.Key}={kv.Value}")
                    ).ToArray()
                );
            } else {
                result.Reference = (value is IList<object> values)
                    ? FnJoin(",", values)
                    : value;
            }

            // check if value must be decrypted
            if(result.HasSecretType) {
                var decoder = AddResource(
                    parent: result,
                    name: "Plaintext",
                    description: null,
                    scope: null,
                    resource: CreateDecryptSecretResourceFor(result),
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: null,
                    pragmas: null
                );
                decoder.Reference = FnGetAtt(decoder.ResourceName, "Plaintext");
                decoder.DiscardIfNotReachable = true;
            }

            // add optional grants
            if(allow != null) {
                AddGrant(result.LogicalId, type, value, allow);
            }
            return result;
        }

        public AModuleItem AddResource(
            AModuleItem parent,
            string name,
            string description,
            IList<string> scope,
            Humidifier.Resource resource,
            string resourceArnAttribute,
            IList<string> dependsOn,
            object condition,
            IList<object> pragmas
        ) {
            var result = new ResourceItem(
                parent: parent,
                name: name,
                description: description,
                scope: scope,
                resource: resource,
                resourceArnAttribute: resourceArnAttribute,
                dependsOn: dependsOn,
                condition: null,
                pragmas: pragmas
            );
            AddItem(result);

            // add condition
            if(condition is string conditionName) {
                result.Condition = conditionName;
            } else if(condition != null) {
                var conditionItem = AddCondition(
                    parent: result,
                    name: "If",
                    description: null,
                    value: condition
                );
                result.Condition = conditionItem.FullName;
            }
            return result;
        }

        public AModuleItem AddResource(
            AModuleItem parent,
            string name,
            string description,
            string type,
            IList<string> scope,
            object allow,
            IDictionary<string, object> properties,
            IList<string> dependsOn,
            string arnAttribute,
            object condition,
            IList<object> pragmas
        ) {

            // create resource item
            var customResource = RegisterCustomResourceNameMapping(new Humidifier.CustomResource(type, properties));
            var result = AddResource(
                parent: parent,
                name: name,
                description: description,
                scope: scope,
                resource: customResource,
                resourceArnAttribute: arnAttribute,
                dependsOn: dependsOn,
                condition: condition,
                pragmas: pragmas
            );

            // validate resource properties
            if(result.HasTypeValidation) {
                ValidateProperties(type, customResource);
            }

            // add optional grants
            if(allow != null) {
                AddGrant(result.LogicalId, type, result.GetExportReference(), allow);
            }
            return result;
        }

        public AModuleItem AddNestedModule(
            AModuleItem parent,
            string name,
            string description,
            string moduleOwner,
            string moduleName,
            VersionInfo moduleVersion,
            string moduleBucketName,
            IList<string> scope,
            object dependsOn,
            IDictionary<string, object> parameters
        ) {
            var sourceBucketName = moduleBucketName ?? FnRef("DeploymentBucketName");
            var moduleParameters = (parameters != null)
                ? new Dictionary<string, object>(parameters)
                : new Dictionary<string, object>();
            AtLocation("Parameters", () => {
                OptionalAdd("LambdaSharpDeadLetterQueueArn", FnRef("Module::DeadLetterQueueArn"));
                OptionalAdd("LambdaSharpLoggingStreamArn", FnRef("Module::LoggingStreamArn"));
                OptionalAdd("LambdaSharpDefaultSecretKeyArn", FnRef("Module::DefaultSecretKeyArn"));
                MandatoryAdd("DeploymentBucketName", sourceBucketName);
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
                    TemplateURL = FnSub("https://${ModuleBucketName}.s3.${AWS::Region}.amazonaws.com/${ModuleOwner}/Modules/${ModuleName}/Versions/${ModuleVersion}/cloudformation.json", new Dictionary<string, object> {
                        ["ModuleOwner"] = moduleOwner,
                        ["ModuleName"] = moduleName,
                        ["ModuleVersion"] = moduleVersion.ToString(),
                        ["ModuleBucketName"] = sourceBucketName
                    }),

                    // TODO (2018-11-29, bjorg): make timeout configurable
                    TimeoutInMinutes = 5
                },
                resourceArnAttribute: null,
                dependsOn: ConvertToStringList(dependsOn),
                condition: null,
                pragmas: null
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

        public AModuleItem AddPackage(
            AModuleItem parent,
            string name,
            string description,
            IList<string> scope,
            IList<KeyValuePair<string, string>> files
        ) {

            // create variable corresponding to the package definition
            var package = new PackageItem(
                parent: parent,
                name: name,
                description: description,
                scope: scope,
                files: files
            );
            AddItem(package);

            // create nested variable for tracking the package-name
            var packageName = AddVariable(
                parent: package,
                name: "PackageName",
                description: null,
                type: "String",
                scope: null,
                value: $"{package.LogicalId}-DRYRUN.zip",
                allow: null,
                encryptionContext: null
            );

            // update the package variable to use the package-name variable
            package.Reference = FnSub($"${{Module::Owner}}/Modules/${{Module::Name}}/Assets/${{{packageName.FullName}}}");
            return package;
        }

        public AModuleItem AddFunction(
            AModuleItem parent,
            string name,
            string description,
            IList<string> scope,
            string project,
            string language,
            IDictionary<string, object> environment,
            IList<AFunctionSource> sources,
            object condition,
            IList<object> pragmas,
            string timeout,
            string runtime,
            string reservedConcurrency,
            string memory,
            string handler,
            object subnets,
            object securityGroups
        ) {

            // create function item
            var function = new FunctionItem(
                parent: parent,
                name: name,
                description: description,
                scope: scope,
                project: project,
                language: language,
                environment: environment ?? new Dictionary<string, object>(),
                sources: sources ?? new AFunctionSource[0],
                condition: null,
                pragmas: pragmas ?? new object[0],
                function: new Humidifier.Lambda.Function {

                    // append version number to function description
                    Description = (description != null)
                        ? description.TrimEnd() + $" (v{_version})"
                        : null,
                    Timeout = timeout,
                    Runtime = runtime,
                    ReservedConcurrentExecutions = reservedConcurrency,
                    MemorySize = memory,
                    Handler = handler,

                    // create optional VPC configuration
                    VpcConfig = ((subnets != null) && (securityGroups != null))
                        ? new Humidifier.Lambda.FunctionTypes.VpcConfig {
                            SubnetIds = subnets,
                            SecurityGroupIds = securityGroups
                        }
                        : null,
                    Role = FnGetAtt("Module::Role", "Arn"),
                    Environment = new Humidifier.Lambda.FunctionTypes.Environment {
                        Variables = new Dictionary<string, dynamic>()
                    },
                    Code = new Humidifier.Lambda.FunctionTypes.Code {
                        S3Bucket = FnRef("DeploymentBucketName")
                    }
                }
            );
            AddItem(function);

            // add condition
            if(condition is string conditionName) {
                function.Condition = conditionName;
            } else if(condition != null) {
                var conditionItem = AddCondition(
                    parent: function,
                    name: "If",
                    description: null,
                    value: condition
                );
                function.Condition = conditionItem.FullName;
            }

            // create nested variable for tracking the package-name
            var packageName = AddVariable(
                parent: function,
                name: "PackageName",
                description: null,
                type: "String",
                scope: null,
                value: $"{function.LogicalId}-DRYRUN.zip",
                allow: null,
                encryptionContext: null
            );
            function.Function.Code.S3Key = FnSub($"${{Module::Owner}}/Modules/${{Module::Name}}/Assets/${{{packageName.FullName}}}");

            // check if function is a finalizer
            var isFinalizer = (parent == null) && (name == "Finalizer");
            if(isFinalizer) {

                // finalizer doesn't need a log-group or registration b/c it gets deleted anyway on failure or teardown
                function.Pragmas = new List<object>(function.Pragmas) {
                    "no-function-registration",
                    "no-dead-letter-queue"
                };

                // NOTE (2018-12-18, bjorg): always set the 'Finalizer' timeout to the maximum limit to prevent ugly timeout scenarios
                function.Function.Timeout = 900;

                // add finalizer invocation (dependsOn will be set later when all resources have been added)
                AddResource(
                    parent: function,
                    name: "Invocation",
                    description: null,
                    scope: null,
                    resource: RegisterCustomResourceNameMapping(new Humidifier.CustomResource("Module::Finalizer") {
                        ["ServiceToken"] = FnGetAtt(function.FullName, "Arn"),
                        ["DeploymentChecksum"] = FnRef("DeploymentChecksum"),
                        ["ModuleVersion"] = _version.ToString()
                    }),
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: condition,
                    pragmas: null
                );
            } else {

                // create function log-group with retention window
                AddResource(
                    parent: function,
                    name: "LogGroup",
                    description: null,
                    scope: null,
                    resource: new Humidifier.Logs.LogGroup {
                        LogGroupName = FnSub($"/aws/lambda/${{{function.ResourceName}}}"),

                        // TODO (2018-09-26, bjorg): make retention configurable
                        //  see https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutRetentionPolicy.html
                        RetentionInDays = 30
                    },
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: condition,
                    pragmas: null
                );
            }
            return function;
        }

        public AModuleItem AddInlineFunction(
            AModuleItem parent,
            string name,
            string description,
            IDictionary<string, object> environment,
            IList<AFunctionSource> sources,
            string condition,
            IList<object> pragmas,
            string timeout,
            string reservedConcurrency,
            string memory,
            object subnets,
            object securityGroups,
            string code
        ) {

            // create inline function item
            var function = new FunctionItem(
                parent: parent,
                name: name,
                description: description,
                scope: new string[0],
                project: "",
                language: "javascript",
                environment: environment ?? new Dictionary<string, object>(),
                sources: sources ?? new AFunctionSource[0],
                condition: condition,
                pragmas: pragmas ?? new object[0],
                function: new Humidifier.Lambda.Function {

                    // append version number to function description
                    Description = (description != null)
                        ? description.TrimEnd() + $" (v{_version})"
                        : null,
                    Timeout = timeout,
                    Runtime = "nodejs8.10",
                    ReservedConcurrentExecutions = reservedConcurrency,
                    MemorySize = memory,
                    Handler = "index.handler",

                    // create optional VPC configuration
                    VpcConfig = ((subnets != null) && (securityGroups != null))
                        ? new Humidifier.Lambda.FunctionTypes.VpcConfig {
                            SubnetIds = subnets,
                            SecurityGroupIds = securityGroups
                        }
                        : null,
                    Role = FnGetAtt("Module::Role", "Arn"),
                    Environment = new Humidifier.Lambda.FunctionTypes.Environment {
                        Variables = new Dictionary<string, dynamic>()
                    },
                    Code = new Humidifier.Lambda.FunctionTypes.Code {
                        ZipFile = code
                    }
                }
            );
            AddItem(function);
            return function;
        }

        public AModuleItem AddCondition(
            AModuleItem parent,
            string name,
            string description,
            object value
        ) {
            return AddItem(new ConditionItem(
                parent: parent,
                name: name,
                description: description,
                value: value
            ));
        }

        public AModuleItem AddMapping(
            AModuleItem parent,
            string name,
            string description,
            IDictionary<string, IDictionary<string, string>> value
        ) {
            return AddItem(new MappingItem(
                parent: parent,
                name: name,
                description: description,
                value: value
            ));
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

        public void VisitAll(ModuleVisitorDelegate visitor) {
            if(visitor == null) {
                throw new ArgumentNullException(nameof(visitor));
            }

            // resolve references in secrets
            AtLocation("Secrets", () => {
                _secrets = (IList<object>)visitor(null, _secrets);
            });

            // resolve references in items
            AtLocation("Items", () => {
                foreach(var item in _items) {
                    AtLocation(item.FullName, () => {
                        item.Visit(visitor);
                    });
                }
            });

            // resolve references in output values
            AtLocation("ResourceStatements", () => {

                // TODO (2019-01-05, bjorg): pass in 'Module::Role' item?
                _resourceStatements = (IList<Humidifier.Statement>)visitor(null, _resourceStatements);
            });
        }

        public bool HasAttribute(AModuleItem item, string attribute) {
            var dependency = _dependencies.Values.FirstOrDefault(d => d.Manifest?.CustomResourceTypes.ContainsKey(item.Type) ?? false);
            if(dependency != null) {
                return dependency.Manifest.CustomResourceTypes.TryGetValue(item.Type, out ModuleManifestCustomResource customResource)
                    && customResource.Response.Any(field => field.Name == attribute);
            }
            return item.HasAttribute(attribute);
        }

        public Module ToModule() {

            // update existing resource statements when they exist
            if(TryGetItem("Module::Role", out AModuleItem moduleRoleItem)) {
                var role = (Humidifier.IAM.Role)((ResourceItem)moduleRoleItem).Resource;
                role.Policies[0].PolicyDocument.Statement = _resourceStatements.ToList();
            }
            return new Module {
                Owner = _owner,
                Name = _name,
                Version = _version,
                Description = _description,
                Pragmas = _pragmas,
                Secrets = _secrets,
                Items = _items,
                Assets = _assets.OrderBy(value => value).ToList(),
                Dependencies = _dependencies.OrderBy(kv => kv.Key).ToList(),
                CustomResourceTypes = _customResourceTypes.OrderBy(kv => kv.Key).ToList(),
                MacroNames = _macroNames.OrderBy(value => value).ToList(),
                ResourceTypeNameMappings = _resourceTypeNameMappings
            };
        }

        private AModuleItem AddItem(AModuleItem item) {
            Validate(Regex.IsMatch(item.Name, CLOUDFORMATION_ID_PATTERN), "name is not valid");

            // set default reference
            if(item.Reference == null) {
                item.Reference = FnRef(item.ResourceName);
            }

            // add item
            if(_itemsByFullName.TryAdd(item.FullName, item)) {
                _items.Add(item);
            } else {
                AddError($"duplicate name '{item.FullName}'");
            }
            return item;
        }

        private void ValidateProperties(
            string awsType,
            IDictionary properties
        ) {
            if(ResourceMapping.CloudformationSpec.ResourceTypes.TryGetValue(awsType, out ResourceType resource)) {
                ValidateProperties("", resource, properties);
            } else if(!awsType.StartsWith("Custom::", StringComparison.Ordinal)) {
                var dependency = _dependencies.Values.FirstOrDefault(d => d.Manifest?.CustomResourceTypes.ContainsKey(awsType) ?? false);
                if(dependency == null) {
                    if(_dependencies.Values.Any(d => d.Manifest == null)) {

                        // NOTE (2018-12-13, bjorg): one or more manifests were not loaded; give the benefit of the doubt
                        AddWarning($"unable to validate properties for {awsType}");
                    } else {
                        AddError($"unrecognized resource type {awsType}");
                    }
                } else if(properties != null) {
                    var definition = dependency.Manifest.CustomResourceTypes[awsType];
                    if(definition != null) {
                        foreach(var key in properties.Keys) {
                            var stringKey = (string)key;
                            if((stringKey != "ServiceToken") && !definition.Request.Any(field => field.Name == stringKey)) {
                                AddError($"unrecognized attribute '{key}' on type {awsType}");
                            }
                        }
                    }
                }
            }

            // local functions
            void ValidateProperties(string prefix, ResourceType currentResource, IDictionary currentProperties) {

                // 'Fn::Transform' can add arbitrary properties at deployment time, so we can't validate the properties at compile time
                if(!currentProperties.Contains("Fn::Transform")) {

                    // check that all required properties are defined
                    foreach(var property in currentResource.Properties.Where(kv => kv.Value.Required)) {
                        if(currentProperties[property.Key] == null) {
                            AddError($"missing property '{prefix + property.Key}");
                        }
                    }
                }

                // check that all defined properties exist
                foreach(DictionaryEntry property in currentProperties) {
                    if(!currentResource.Properties.TryGetValue((string)property.Key, out PropertyType propertyType)) {
                        AddError($"unrecognized property '{prefix + property.Key}'");
                    } else {
                        switch(propertyType.Type) {
                        case "List":
                            if(!(property.Value is IList nestedList)) {
                                AddError($"property type mismatch for '{prefix + property.Key}', expected a list [{property.Value?.GetType().Name ?? "<null>"}]");
                            } else if(propertyType.ItemType != null) {
                                var nestedResource = ResourceMapping.CloudformationSpec.PropertyTypes[awsType + "." + propertyType.ItemType];
                                ValidateList(prefix + property.Key, nestedResource, ListToEnumerable(nestedList));
                            } else {

                                // TODO (2018-12-06, bjorg): validate list items using the primitive type
                            }
                            break;
                        case "Map":
                            if(!(property.Value is IDictionary nestedProperties1)) {
                                AddError($"property type mismatch for '{prefix + property.Key}', expected a map [{property.Value?.GetType().FullName ?? "<null>"}]");
                            } else if(propertyType.ItemType != null) {
                                var nestedResource = ResourceMapping.CloudformationSpec.PropertyTypes[awsType + "." + propertyType.ItemType];
                                ValidateList(prefix + property.Key, nestedResource, DictionaryToEnumerable(nestedProperties1));
                            } else {

                                // TODO (2018-12-06, bjorg): validate map entries using the primitive type
                            }
                            break;
                        case null:

                            // TODO (2018-12-06, bjorg): validate property value with the primitive type
                            break;
                        default:
                            if(!(property.Value is IDictionary nestedProperties2)) {
                                AddError($"property type mismatch for '{prefix + property.Key}', expected a map [{property.Value?.GetType().FullName ?? "<null>"}]");
                            } else {
                                var nestedResource = ResourceMapping.CloudformationSpec.PropertyTypes[awsType + "." + propertyType.Type];
                                ValidateProperties(prefix + property.Key + ".", nestedResource, nestedProperties2);
                            }
                            break;
                        }
                    }
                }
            }

            void ValidateList(string prefix, ResourceType currentResource, IEnumerable<KeyValuePair<string, object>> items) {
                foreach(var item in items) {
                    if(!(item.Value is IDictionary nestedProperties)) {
                        AddError($"property type mismatch for '{prefix + item.Key}', expected a map [{item.Value?.GetType().FullName ?? "<null>"}]");
                    } else {
                        ValidateProperties(prefix + item.Key, currentResource, nestedProperties);
                    }
                }
            }

            IEnumerable<KeyValuePair<string, object>> DictionaryToEnumerable(IDictionary dictionary) {
                var result = new List<KeyValuePair<string, object>>();
                foreach(DictionaryEntry entry in dictionary) {
                    result.Add(new KeyValuePair<string, object>("." + entry.Key, entry.Value));
                }
                return result;
            }

            IEnumerable<KeyValuePair<string, object>> ListToEnumerable(IList list) {
                var result = new List<KeyValuePair<string, object>>();
                var index = 0;
                foreach(var item in list) {
                    result.Add(new KeyValuePair<string, object>($"{++index}".ToString(), item));
                }
                return result;
            }
        }

        private Humidifier.CustomResource CreateDecryptSecretResourceFor(AModuleItem item)
            => RegisterCustomResourceNameMapping(new Humidifier.CustomResource("Module::DecryptSecret") {
                ["ServiceToken"] = FnGetAtt("Module::DecryptSecretFunction", "Arn"),
                ["Ciphertext"] = FnRef(item.FullName)
            });

        private Humidifier.CustomResource RegisterCustomResourceNameMapping(Humidifier.CustomResource customResource) {
            if(customResource.AWSTypeName != customResource.OriginalTypeName) {
                _resourceTypeNameMappings[customResource.AWSTypeName] = customResource.OriginalTypeName;
            }
            return customResource;
        }
    }
}