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

        public AResource AddEntry(AResource resource) {
            resource.FullName = resource.Name;
            resource.ResourceName = $"@{resource.Name}";
            _module.Resources.Add(resource);
            return resource;
        }

        public AResource AddEntry(AResource parent, AResource resource) {
            if(parent == null) {
                return AddEntry(resource);
            }
            resource.FullName = parent.FullName + "::" + resource.Name;
            resource.ResourceName = parent.ResourceName + resource.Name;
            _module.Resources.Add(resource);
            return resource;
        }

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

        public void AddVariable(string fullName, object reference, object scope = null) {
            _module.Variables.Add(fullName, new ModuleVariable {
                FullName = fullName ?? throw new ArgumentNullException(nameof(fullName)),
                Scope = ConvertScope(scope),
                Reference = reference ?? throw new ArgumentNullException(nameof(reference))
            });
        }

        public ModuleVariable GetVariable(string fullName) => _module.Variables[fullName];

        public AResource AddInput(
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
                Default = defaultValue,
                ConstraintDescription = constraintDescription,
                AllowedPattern = allowedPattern,
                AllowedValues = allowedValues,
                MaxLength = maxLength,
                MaxValue = maxValue,
                MinLength = minLength,
                MinValue = minValue,

                // set AParameter fields
                Description = description,

                // set AInputParamete fields
                Type = type ?? "String",
                Section = section ?? "Module Settings",
                Label = label ?? StringEx.PascalCaseToLabel(name),
                NoEcho = noEcho
            });

            // check if a conditional managed resource is associated with the input parameter
            if((awsType == null) && (awsAllow == null)) {

                // register input reference
                var reference = FnRef(result.ResourceName);
                AddVariable(result.FullName, reference, scope);
            } else if(defaultValue == null) {

                // register input reference
                var reference = FnRef(result.ResourceName);
                AddVariable(result.FullName, reference, scope);

                // request input parameter resource grants
                AddGrant(result.LogicalId, awsType, reference, awsAllow);
            } else {

                // create conditional managed resource
                var condition = $"{result.LogicalId}Created";
                AddCondition(condition, FnEquals(FnRef(result.ResourceName), defaultValue));
                var instance = AddEntry(result, new CloudFormationResourceParameter {
                    Name = "Resource",
                    Resource = CreateResource(awsType, awsProperties, condition)
                });

                // register input parameter reference
                var reference = FnIf(
                    condition,
                    ResourceMapping.GetArnReference(awsType, instance.ResourceName),
                    FnRef(result.ResourceName)
                );
                AddVariable(result.FullName, reference, scope);

                // request input parameter or conditional managed resource grants
                AddGrant(instance.LogicalId, awsType, reference, awsAllow);
            }
            return result;
        }

        public AResource AddImport(
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
            var rootParameter = _module.Resources.FirstOrDefault(p => p.Name == exportModule);
            if(rootParameter == null) {

                // create root collection entry
                rootParameter = AddEntry(new ValueParameter {
                    Name = exportModule,
                    Description = $"{exportModule} cross-module references"
                });

                // register root collection reference
                AddVariable(rootParameter.FullName, "");
            }

            // create import parameter entry
            var result = AddEntry(rootParameter, new InputParameter {
                Name = exportName,
                Default = "$" + import,
                ConstraintDescription = "must either be a cross-module import reference or a non-blank value",
                AllowedPattern =  @"^.+$",

                // set AParameter fields
                Description = description,

                // set AInputParamete fields
                Type = type ?? "String",
                Section = section ?? "Module Settings",

                // TODO (2018-11-11, bjorg): do we really want to use the cross-module reference when converting to a label?
                Label = label ?? StringEx.PascalCaseToLabel(import),
                NoEcho = noEcho
            });

            // register import parameter reference
            var condition = $"{result.LogicalId}IsImport";
            AddCondition(condition, FnEquals(FnSelect("0", FnSplit("$", FnRef(result.ResourceName))), ""));
            var reference = AModelProcessor.FnIf(
                condition,
                FnImportValue(FnSub("${DeploymentPrefix}${Import}", new Dictionary<string, object> {
                    ["Import"] = FnSelect("1", FnSplit("$", FnRef(result.ResourceName)))
                })),
                FnRef(result.ResourceName)
            );
            AddVariable(result.FullName, reference, scope);

            // check if resource grants is associated with the import parameter
            if((awsType != null) || (awsAllow != null)) {

                // request input parameter or conditional managed resource grants
                AddGrant(result.LogicalId, awsType, reference, awsAllow);
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

        private IList<string> ConvertScope(object scope) {
            if(scope == null) {
                return new string[0];
            }
            return AtLocation("Scope", () => {
                return (scope == null)
                    ? new List<string>()
                    : ConvertToStringList(scope);
            }, new List<string>());
        }
    }
}