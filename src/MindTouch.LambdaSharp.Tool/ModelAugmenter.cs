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

using System.Linq;
using MindTouch.LambdaSharp.Tool.Model.AST;

namespace MindTouch.LambdaSharp.Tool
{
    public class ModelAugmenter : AModelProcessor {

        //--- Constructors ---
        public ModelAugmenter(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Augment(ModuleNode module) {

            // add LambdaSharp Module Options
            var section = "LambdaSharp Module Options";
            module.Inputs.Add(new InputNode {
                Parameter = "Secrets",
                Section = section,
                Label = "Secret Keys (ARNs)",
                Description = "Comma-separated list of optional secret keys",
                Default = ""
            });

            // add standard parameters (unless requested otherwise)
            if(!module.HasPragma("no-lambdasharp-dependencies")) {

                // add LambdaSharp Module Internal Dependencies
                section = "LambdaSharp Dependencies";
                module.Inputs.Add(new InputNode {
                    Import = "LambdaSharp::DeadLetterQueueArn",
                    Section = section,
                    Label = "Dead Letter Queue (ARN)",
                    Description = "Dead letter queue for functions"
                });
                module.Inputs.Add(new InputNode {
                    Import = "LambdaSharp::LoggingStreamArn",
                    Section = section,
                    Label = "Logging Stream (ARN)",
                    Description = "Logging kinesis stream for functions"
                });
                module.Inputs.Add(new InputNode {
                    Import = "LambdaSharp::DefaultSecretKeyArn",
                    Section = section,
                    Label = "Secret Key (ARN)",
                    Description = "Default secret key for functions"
                });
            }

            // add LambdaSharp Deployment Settings
            section = "LambdaSharp Deployment Settings (DO NOT MODIFY)";
            module.Inputs.Add(new InputNode {
                Parameter = "DeploymentBucketName",
                Section = section,
                Label = "Deployment S3 Bucket",
                Description = "Source deployment S3 bucket name"
            });
            module.Inputs.Add(new InputNode {
                Parameter = "DeploymentPrefix",
                Section = section,
                Label = "Deployment Prefix",
                Description = "Module deployment prefix"
            });
            module.Inputs.Add(new InputNode {
                Parameter = "DeploymentPrefixLowercase",
                Section = section,
                Label = "Deployment Prefix (lowercase)",
                Description = "Module deployment prefix (lowercase)"
            });
            module.Inputs.Add(new InputNode {
                Parameter = "DeploymentParent",
                Section = section,
                Label = "Parent Stack Name",
                Description = "Parent stack name for nested deployments, blank otherwise",
                Default = ""
            });

            // add module variables
            var moduleVar = new ParameterNode {
                Var = "Module",
                Description = "LambdaSharp module information",
                Variables = new[] {
                    new ParameterNode {
                        Var = "Id",
                        Description = "LambdaSharp module id",
                        Value = FnRef("AWS::StackName")
                    },
                    new ParameterNode {
                        Var = "Name",
                        Description = "Module name",
                        Value = module.Module
                    },
                    new ParameterNode {
                        Var = "Version",
                        Description = "Module version",
                        Value = module.Version.ToString()
                    }
                }.ToList()
            };
            module.Variables.Add(moduleVar);
            if(!module.Pragmas.Contains("no-lambdasharp-dependencies")) {
                moduleVar.Variables.Add(new ParameterNode {
                    Var = "DeadLetterQueueArn",
                    Description = "LambdaSharp Dead Letter Queue",
                    Value = FnRef("LambdaSharp::DeadLetterQueueArn")
                });
                moduleVar.Variables.Add(new ParameterNode {
                    Var = "LoggingStreamArn",
                    Description = "LambdaSharp Logging Stream",
                    Value = FnRef("LambdaSharp::LoggingStreamArn")
                });
            }
            if(module.Functions.Any(function => function.Sources.Any(source => (source.Api != null) || (source.SlackCommand != null)))) {
//                varModuleNode.Variables.AddRange(new List<AParameter> {

                    // TODO (2010-10-19, bjorg): figure out how to make this work

                    // new CloudFormationResourceParameter {
                    //     Scope = new List<string>(),
                    //     Name = "RestApi",
                    //     ResourceName = "ModuleRestApi",
                    //     Description = $"{_module.Name} API (v{_module.Version})",
                    //     Reference = FnRef("ModuleRestApi"),
                    //     Resource = new Resource {
                    //         Type = "AWS::ApiGateway::RestApi",
                    //         ResourceReferences = new List<object>(),
                    //         Properties = new Dictionary<string, object> {
                    //             ["Name"] = FnSub("${AWS::StackName} Module API"),
                    //             ["Description"] = $"{_module.Name} API (v{_module.Version})",
                    //             ["FailOnWarnings"] = true
                    //         }
                    //     }
                    // },

                    // // TODO (2018-10-30, bjorg): convert to a resource
                    // new ValueParameter {
                    //     Scope = new List<string>(),
                    //     Name = "RestApiStage",
                    //     ResourceName = "ModuleRestApiStage",
                    //     Description = "LambdaSharp module REST API",
                    //     Reference = FnRef("ModuleRestApiStage")
                    // },
                    // new ValueParameter {
                    //     Scope = new List<string>(),
                    //     Name = "RestApiUrl",
                    //     ResourceName = "ModuleRestApiUrl",
                    //     Description = "LambdaSharp module REST API URL",
                    //     Reference = FnSub("https://${Module::RestApi}.execute-api.${AWS::Region}.${AWS::URLSuffix}/LATEST/")
                    // }
//                });
            }
        }
    }
}