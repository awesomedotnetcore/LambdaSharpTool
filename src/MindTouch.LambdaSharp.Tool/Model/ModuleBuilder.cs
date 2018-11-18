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

        //--- Types ---
        public class Entry<TResource> where TResource : AResource {

            //--- Fields ---
            private readonly ModuleBuilder _builder;
            private readonly ModuleEntry<TResource> _entry;

            //--- Constructors ---
            public Entry(ModuleBuilder builder, ModuleEntry<TResource> entry) {
                _builder = builder ?? throw new ArgumentNullException(nameof(builder));
                _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            }

            //--- Properties ---
            public string FullName => _entry.FullName;
            public string Description => _entry.Description;
            public string ResourceName => _entry.ResourceName;
            public string LogicalId => _entry.LogicalId;
            public TResource Resource => _entry.Resource;

            public object Reference {
                get => _entry.Reference;
                set => _entry.Reference = value;
            }

            //--- Methods ---
            public Entry<TChild> AddEntry<TChild>(TChild resource) where TChild : AResource {
                return _builder.AddEntry<TChild, TResource>(this, resource);
            }

            public Entry<TResourceType> Cast<TResourceType>() where TResourceType : AResource
                => new Entry<TResourceType>(_builder, _entry.Cast<TResourceType>());
        }

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

        public Entry<TResource> AddEntry<TResource>(TResource resource) where TResource : AResource
            => AddEntry<TResource, AResource>(null, resource);

        public AResource GetEntry(string fullName) => _module.GetResource(fullName);

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

        public Entry<AResource> AddVariable(string fullName, string description, object reference, IList<string> scope = null) {
            var entry = new ModuleEntry<AResource>(
                fullName,
                description,
                reference ?? throw new ArgumentNullException(nameof(reference)),
                scope,
                resource: null
            );
            _module.Entries.Add(fullName, entry);
            return new Entry<AResource>(this, entry);
        }

        public object GetVariable(string fullName) => _module.Entries[fullName].Reference;

        public Entry<InputParameter> AddInput(
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
            IDictionary<string, object> awsProperties = null
        ) {

            // create input parameter entry
            var result = AddEntry(new InputParameter {
                Name = name,
                Description = description,
                Scope = ConvertScope(scope),
                Section = section ?? "Module Settings",
                Label = label ?? StringEx.PascalCaseToLabel(name),
                Parameter = new Humidifier.Parameter {
                    Type = ConvertInputType(type),
                    Description = description,
                    Default = defaultValue,
                    ConstraintDescription = constraintDescription,
                    AllowedPattern = allowedPattern,
                    AllowedValues = allowedValues.ToList(),
                    MaxLength = maxLength,
                    MaxValue = maxValue,
                    MinLength = minLength,
                    MinValue = minValue,
                    NoEcho = noEcho
                }
            });

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
                var instance = AddEntry(result, new CloudFormationResourceParameter {
                    Name = "Resource",
                    Resource = CreateResource(awsType, awsProperties, condition)
                });

                // register input parameter reference
                result.Reference = FnIf(
                    condition,
                    ResourceMapping.GetArnReference(awsType, instance.ResourceName),
                    FnRef(result.ResourceName)
                );

                // request input parameter or conditional managed resource grants
                AddGrant(instance.LogicalId, awsType, result.Reference, awsAllow);
            }
            return result;
        }

        public Entry<InputParameter> AddImport(
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
            var rootParameter = _module.Entries.TryGetValue(exportModule, out ModuleEntry<AResource> existingEntry)
                ? new Entry<AResource>(this, existingEntry)
                : AddVariable(exportModule, $"{exportModule} cross-module references", "");

            // create import parameter entry
            var result = rootParameter.AddEntry(new InputParameter {
                Name = exportName,
                Description = description,
                Scope = ConvertScope(scope),
                Section = section ?? "Module Settings",

                // TODO (2018-11-11, bjorg): do we really want to use the cross-module reference when converting to a label?
                Label = label ?? StringEx.PascalCaseToLabel(import),
                Parameter = new Humidifier.Parameter {
                    Type = ConvertInputType(type),
                    Description = description,
                    Default = "$" + import,
                    ConstraintDescription = "must either be a cross-module import reference or a non-blank value",
                    AllowedPattern =  @"^.+$",
                    NoEcho = noEcho
                }
            });

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
                Handler = handler
            });
        }

        public void AddMacro(string macroName, string description, string handler) {
            _module.Outputs.Add(new MacroOutput {
                Macro = macroName,
                Description = description,
                Handler = handler
            });
        }

        public void AddGrant(string sid, string awsType, object reference, object awsAllow) {

            // resolve shorthands and deduplicate statements
            var allowStatements = new List<string>();
            foreach(var allowStatement in ConvertToStringList(awsAllow)) {
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

            // add grant
            _module.Grants.Add(new ModuleGrant {
                Sid = sid,
                References = ResourceMapping.ExpandResourceReference(awsType, reference),
                Allow = allowStatements.Distinct().OrderBy(text => text).ToList()
            });
        }

        public void AddCondition(string name, object condition) => _module.Conditions.Add(name, condition);

        public void AddResourceStatement(Humidifier.Statement statement) {
            _module.ResourceStatements.Add(statement);
        }

        public IList<string> ConvertScope(object scope) {
            if(scope == null) {
                return new string[0];
            }
            return AtLocation("Scope", () => {
                return (scope == null)
                    ? new List<string>()
                    : ConvertToStringList(scope);
            }, new List<string>());
        }

        private Entry<TResource> AddEntry<TResource, TParent>(
            Entry<TParent> parent,
            TResource resource
        ) where TResource : AResource where TParent : AResource {
            var fullName = (parent == null)
                ? resource.Name
                : parent.FullName + "::" + resource.Name;

            // create entry
            var entry = new ModuleEntry<TResource>(fullName, resource.Description, resource.Reference, resource.Scope, resource);
            _module.Entries.Add(fullName, entry.Cast<AResource>());
            if(entry.Reference == null) {
                entry.Reference = FnRef(entry.ResourceName);
            }

            // create entry
            return new Entry<TResource>(this, entry);
        }

        private string ConvertInputType(string type)
            => ((type == null) || (type == "Secret"))
                ? "String"
                : type;
    }
}