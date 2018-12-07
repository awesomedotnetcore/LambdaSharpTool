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
using System.IO;
using System.Linq;
using Humidifier;
using Humidifier.Json;
using Humidifier.Logs;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MindTouch.LambdaSharp.Tool.Build {

    public class ModelStackGenerator : AModelProcessor {

        //--- Fields ---
        private Module _module;
        private Stack _stack;

        //--- Constructors ---
        public ModelStackGenerator(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public string Generate(Module module, string gitSha) {
            _module = module;

            // stack header
            _stack = new Stack {
                AWSTemplateFormatVersion = "2010-09-09",
                Description = (_module.Description != null)
                    ? _module.Description.TrimEnd() + $" (v{_module.Version})"
                    : null
            };

            // add conditions
            foreach(var condition in _module.Conditions) {
                _stack.Add(condition.Key, new Condition(condition.Value));
            }

            // add resources
            foreach(var entry in _module.Entries) {
                AddResource(entry);
            }

            // add outputs
            _stack.Add("ModuleName", new Humidifier.Output {
                Value = _module.Name
            });
            _stack.Add("ModuleVersion", new Humidifier.Output {
                Value = _module.Version.ToString()
            });
            foreach(var output in module.Outputs) {
                AddOutput(output);
            }

            // add interface for presenting inputs
            var inputParameters = _module.Entries.OfType<InputEntry>();
            _stack.AddTemplateMetadata("AWS::CloudFormation::Interface", new Dictionary<string, object> {
                ["ParameterLabels"] = inputParameters.ToDictionary(input => input.LogicalId, input => new Dictionary<string, object> {
                    ["default"] = input.Label
                }),
                ["ParameterGroups"] = inputParameters
                    .GroupBy(input => input.Section)
                    .Select(section => new Dictionary<string, object> {
                        ["Label"] = new Dictionary<string, string> {
                            ["default"] = section.Key
                        },
                        ["Parameters"] = section.Select(input => input.LogicalId).ToList()
                    }
                )
            });

            // add module manifest
            _stack.AddTemplateMetadata("LambdaSharp::Manifest", new ModuleManifest {
                ModuleName = module.Name,
                ModuleVersion = module.Version.ToString(),
                RuntimeCheck = !module.HasPragma("no-runtime-version-check"),
                Hash = GenerateStackHash(),
                GitSha = gitSha ?? "",
                Assets = module.Assets.ToList(),
                Dependencies = module.Dependencies.Select(dependency => new ModuleManifestDependency {
                    ModuleName = dependency.Value.ModuleName,
                    MinVersion = dependency.Value.MinVersion,
                    MaxVersion = dependency.Value.MaxVersion,
                    BucketName = dependency.Value.BucketName
                }).OrderBy(dependency => dependency.ModuleName).ToList(),
                CustomResourceTypes = new Dictionary<string, ModuleCustomResourceProperties>(module.CustomResourceTypes),
                MacroNames = module.MacroNames.ToList(),
                ResourceFullNames = module.Entries

                    // we only ned to worry about resource names
                    .Where(entry => (entry is AResourceEntry))

                    // we only care about entries where the logical ID and full-name don't match
                    .Where(entry => entry.LogicalId != entry.FullName)
                    .ToDictionary(entry => entry.LogicalId, entry => entry.FullName)
            });

            // generate JSON template
            return new JsonStackSerializer().Serialize(_stack);
        }

        private void AddOutput(AOutput output) {
            switch(output) {
            case ExportOutput exportOutput:
                _stack.Add(exportOutput.Name, new Humidifier.Output {
                    Description = exportOutput.Description,
                    Value = exportOutput.Value,
                    Export = new Dictionary<string, dynamic> {
                        ["Name"] = Fn.Sub($"${{AWS::StackName}}::{exportOutput.Name}")
                    }
                });
                break;
            case CustomResourceHandlerOutput customResourceHandlerOutput:
                _stack.Add($"{customResourceHandlerOutput.CustomResourceType.ToIdentifier()}Handler", new Humidifier.Output {
                    Description = customResourceHandlerOutput.Description,
                    Value = customResourceHandlerOutput.Handler,
                    Export = new Dictionary<string, dynamic> {
                        ["Name"] = Fn.Sub($"${{DeploymentPrefix}}CustomResource-{customResourceHandlerOutput.CustomResourceType}")
                    }
                });
                break;
            default:
                throw new InvalidOperationException($"cannot generate output for this type: {output?.GetType()}");
            }
        }

        private void AddResource(AModuleEntry entry) {
            var logicalId = entry.LogicalId;
            switch(entry) {
            case VariableEntry _:
            case PackageEntry _:

                // nothing to do
                break;
            case ResourceEntry resourceEntry:
                _stack.Add(
                    logicalId,
                    resourceEntry.Resource,
                    resourceEntry.Condition,
                    dependsOn: resourceEntry.DependsOn.ToArray()
                );
                break;
            case InputEntry inputEntry:
                _stack.Add(logicalId, inputEntry.Parameter);
                break;
            case FunctionEntry functionEntry:
                _stack.Add(functionEntry.LogicalId, functionEntry.Function);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry, "unknown parameter type");
            }
        }

        private string GenerateStackHash() {

            // convert stack to string using the Humidifier serializer
            var json = new JsonStackSerializer().Serialize(_stack);

            // parse json into a generic object
            var value = JObject.Parse(json);

            // convert value to json, but sort the properties to achieve a stable hash
            json = JsonConvert.SerializeObject(OrderFields(value));
            return (json + ModuleManifest.CurrentVersion).ToMD5Hash();
        }

        private JObject OrderFields(JObject value) {
            var result = new JObject();
            foreach(var property in value.Properties().ToList().OrderBy(property => property.Name)) {
                result.Add(property.Name, (property.Value is JObject propertyValue)
                    ? OrderFields(propertyValue)
                    : property.Value
                );
            }
            return result;
        }

    }
}