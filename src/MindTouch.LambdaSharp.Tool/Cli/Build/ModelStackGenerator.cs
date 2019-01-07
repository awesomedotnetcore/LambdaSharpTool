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

namespace MindTouch.LambdaSharp.Tool.Cli.Build {

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

            // add outputs
            _stack.Add("ModuleInfo", new Humidifier.Output {
                Value = _module.FullName + ":" + _module.Version.ToString()
            });

            // add items
            foreach(var item in _module.Items) {
                AddItem(item);
            }

            // add interface for presenting inputs
            var inputParameters = _module.Items.OfType<ParameterItem>();
            _stack.AddTemplateMetadata("AWS::CloudFormation::Interface", new Dictionary<string, object> {
                ["ParameterLabels"] = inputParameters.ToDictionary(
                    input => input.LogicalId,
                    input => new Dictionary<string, object> {
                        ["default"] = $"[{input.Type}] {input.Label}"
                    }
                ),
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
            var manifest = new ModuleManifest {
                ModuleInfo = module.Info,
                ParameterSections = inputParameters
                    .GroupBy(input => input.Section)
                    .Where(group => group.Key != "LambdaSharp Deployment Settings (DO NOT MODIFY)")
                    .Select(group => new ModuleManifestParameterSection {
                        Title = group.Key,
                        Parameters = group.Select(input => new ModuleManifestParameter {
                            Name = input.Name,
                            Type = input.Type,
                            Label = input.Label,
                            Default = input.Parameter.Default
                        }).ToList()
                    }).ToList(),
                RuntimeCheck = module.HasRuntimeCheck,
                Assets = module.Assets.ToList(),
                Dependencies = module.Dependencies.Select(dependency => new ModuleManifestDependency {
                    ModuleFullName = dependency.Value.ModuleFullName,
                    MinVersion = dependency.Value.MinVersion,
                    MaxVersion = dependency.Value.MaxVersion,
                    BucketName = dependency.Value.BucketName
                }).OrderBy(dependency => dependency.ModuleFullName).ToList(),
                CustomResourceTypes = new Dictionary<string, ModuleManifestCustomResource>(module.CustomResourceTypes),
                MacroNames = module.MacroNames.ToList(),
                ResourceTypeNameMappings = module.ResourceTypeNameMappings
                    .Where(kv => _stack.Resources.Any(resource => resource.Value.AWSTypeName == kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                ResourceNameMappings = module.Items

                    // we only ned to worry about resource names
                    .Where(item => _stack.Resources.ContainsKey(item.LogicalId))

                    // we only care about items where the logical ID and full-name don't match
                    .Where(item => item.LogicalId != item.FullName)
                    .ToDictionary(item => item.LogicalId, item => item.FullName)
            };
            _stack.AddTemplateMetadata("LambdaSharp::Manifest", manifest);

            // update template with template hash
            var templateHash = GenerateCloudFormationTemplateHash();
            manifest.Hash = templateHash;
            manifest.GitSha = gitSha ?? "";
            _stack.Parameters["DeploymentChecksum"].Default = templateHash;

            // generate JSON template
            return new JsonStackSerializer().Serialize(_stack);
        }

        private void AddItem(AModuleItem item) {
            var logicalId = item.LogicalId;
            switch(item) {
            case VariableItem _:
            case PackageItem _:
                if(item.IsPublic) {
                    AddExport(item);
                }
                break;
            case ResourceItem resourceItem:
                _stack.Add(
                    logicalId,
                    resourceItem.Resource,
                    resourceItem.Condition,
                    dependsOn: resourceItem.DependsOn.ToArray()
                );
                if(item.IsPublic) {
                    AddExport(item);
                }
                break;
            case ParameterItem parameterItem:
                _stack.Add(logicalId, parameterItem.Parameter);
                if(item.IsPublic) {
                    AddExport(item);
                }
                break;
            case FunctionItem functionItem:
                _stack.Add(
                    functionItem.LogicalId,
                    functionItem.Function,
                    functionItem.Condition,
                    dependsOn: functionItem.DependsOn.ToArray()
                );
                if(item.IsPublic) {
                    AddExport(item);
                }
                break;
            case ConditionItem conditionItem:
                _stack.Add(conditionItem.LogicalId, new Condition(conditionItem.Reference));
                break;
            case MappingItem mappingItem: {
                    var mapping = new Mapping();
                    foreach(var level1Mapping in mappingItem.Mapping) {
                        mapping[level1Mapping.Key] = level1Mapping.Value.ToDictionary(
                            level2Mapping => level2Mapping.Key,
                            level2Mapping => level2Mapping.Value
                        );
                    }
                    _stack.Add(mappingItem.LogicalId, mapping);
                }
                break;
            case ResourceTypeItem resourceTypeItem:
                _stack.Add(resourceTypeItem.LogicalId, new Humidifier.Output {
                    Description = resourceTypeItem.Description,
                    Value = resourceTypeItem.Handler,
                    Export = new Dictionary<string, dynamic> {
                        ["Name"] = Fn.Sub($"${{DeploymentPrefix}}CustomResource-{resourceTypeItem.CustomResourceType}")
                    }
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item, "unknown parameter type");
            }

            // local functions
            void AddExport(AModuleItem exportItem) {
                _stack.Add(exportItem.LogicalId, new Humidifier.Output {
                    Description = exportItem.Description,
                    Value = exportItem.GetExportReference(),
                    Export = new Dictionary<string, dynamic> {
                        ["Name"] = Fn.Sub($"${{AWS::StackName}}::{exportItem.FullName}")
                    }
                });
            }
        }

        private string GenerateCloudFormationTemplateHash() {

            // convert stack to string using the Humidifier serializer
            var json = new JsonStackSerializer().Serialize(_stack);

            // parse json into a generic object
            var value = JObject.Parse(json);

            // convert value to json, but sort the properties to achieve a stable hash
            return JsonConvert.SerializeObject(OrderFields(value)).ToMD5Hash();
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