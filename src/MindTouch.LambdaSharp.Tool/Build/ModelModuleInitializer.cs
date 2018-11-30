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
using System.Linq;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool.Build {
    using static ModelFunctions;

    public class ModelModuleInitializer : AModelProcessor {

        //--- Fields ---
        private ModuleBuilder _builder;

        //--- Constructors ---
        public ModelModuleInitializer(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Initialize(ModuleBuilder builder) {
            _builder = builder;

            // add module variables
            var moduleEntry = _builder.AddValue(
                parent: null,
                name: "Module",
                description: "Module Variables",
                reference: "",
                scope: null,
                isSecret: false
            );
            _builder.AddValue(
                parent: moduleEntry,
                name: "Id",
                description: "Module ID",
                reference: FnRef("AWS::StackName"),
                scope: null,
                isSecret: false
            );
            _builder.AddValue(
                parent: moduleEntry,
                name: "Name",
                description: "Module Name",
                reference: _builder.Name,
                scope: null,
                isSecret: false
            );
            _builder.AddValue(
                parent: moduleEntry,
                name: "Version",
                description: "Module Version",
                reference: _builder.Version.ToString(),
                scope: null,
                isSecret: false
            );

            // add LambdaSharp Module Options
            var section = "LambdaSharp Module Options";
            _builder.AddInput(
                name: "Secrets",
                section: section,
                label: "Secret Keys (ARNs)",
                description: "Comma-separated list of optional secret keys",
                defaultValue: ""
            );
            _builder.AddCondition("SecretsIsEmpty", FnEquals(FnRef("Secrets"), ""));

            // add standard parameters (unless requested otherwise)
            if(!_builder.HasPragma("no-lambdasharp-dependencies")) {

                // add LambdaSharp Module Internal Dependencies
                section = "LambdaSharp Dependencies";
                _builder.AddImport(
                    import: "LambdaSharp::DeadLetterQueueArn",
                    section: section,
                    label: "Dead Letter Queue (ARN)",
                    description: "Dead letter queue for functions"
                );
                _builder.AddImport(
                    import: "LambdaSharp::LoggingStreamArn",
                    section: section,
                    label: "Logging Stream (ARN)",
                    description: "Logging kinesis stream for functions"
                );
                _builder.AddImport(
                    import: "LambdaSharp::DefaultSecretKeyArn",
                    section: section,
                    label: "Secret Key (ARN)",
                    description: "Default secret key for functions"
                );
                _builder.AddSecret(FnRef("Module::DefaultSecretKeyArn"));

                // add lambdasharp imports
                _builder.AddValue(
                    parent: moduleEntry,
                    name: "DeadLetterQueueArn",
                    description: "Module Dead Letter Queue (ARN)",
                    reference: FnRef("LambdaSharp::DeadLetterQueueArn"),
                    scope: null,
                    isSecret: false
                );
                _builder.AddValue(
                    parent: moduleEntry,
                    name: "LoggingStreamArn",
                    description: "Module Logging Stream (ARN)",
                    reference: FnRef("LambdaSharp::LoggingStreamArn"),
                    scope: null,
                    isSecret: false
                );
                _builder.AddValue(
                    parent: moduleEntry,
                    name: "DefaultSecretKeyArn",
                    description: "Module Default Secret Key (ARN)",
                    reference: FnRef("LambdaSharp::DefaultSecretKeyArn"),
                    scope: null,
                    isSecret: false
                );

                // permissions needed for dead-letter queue
                _builder.AddGrant(
                    sid: "ModuleDeadLetterQueueLogging",
                    awsType: null,
                    reference: FnRef("Module::DeadLetterQueueArn"),
                    allow: new[] {
                        "sqs:SendMessage"
                    }
                );
            }

            // add decryption permission for secrets
            var secretsReference = _builder.Secrets.Any()
                ? FnSplit(
                    ",",
                    FnIf(
                        "SecretsIsEmpty",
                        FnJoin(",", _builder.Secrets),
                        FnJoin(
                            ",",
                            _builder.Secrets.Append(FnRef("Secrets")).ToList()
                        )
                    )
                )
                : FnIf(
                    "SecretsIsEmpty",

                    // NOTE (2018-11-26, bjorg): we use a dummy KMS key, because an empty value would fail
                    "arn:aws:kms:${AWS::Region}:${AWS::AccountId}:key/12345678-1234-1234-1234-123456789012",
                    FnSplit(",", FnRef("Secrets"))
                );
            _builder.AddGrant(
                sid: "SecretsDecryption",
                awsType: null,
                reference: secretsReference,
                allow: new[] {
                    "kms:Decrypt",
                    "kms:Encrypt",
                    "kms:GenerateDataKey",
                    "kms:GenerateDataKeyWithoutPlaintext"
                }
            );

            // add LambdaSharp Deployment Settings
            section = "LambdaSharp Deployment Settings (DO NOT MODIFY)";
            _builder.AddInput(
                name: "DeploymentBucketName",
                section: section,
                label: "Deployment S3 Bucket",
                description: "Source deployment S3 bucket name"
            );
            _builder.AddInput(
                name: "DeploymentPrefix",
                section: section,
                label: "Deployment Prefix",
                description: "Module deployment prefix"
            );
            _builder.AddInput(
                name: "DeploymentPrefixLowercase",
                section: section,
                label: "Deployment Prefix (lowercase)",
                description: "Module deployment prefix (lowercase)"
            );
            _builder.AddInput(
                name: "DeploymentParent",
                section: section,
                label: "Parent Stack Name",
                description: "Parent stack name for nested deployments, blank otherwise",
                defaultValue: ""
            );

            // add module registration
            if(!_builder.HasPragma("no-module-registration")) {
                _builder.AddResource(
                    parent: moduleEntry,
                    name: "Registration",
                    description: null,
                    scope: null,
                    resource: new Humidifier.CustomResource("LambdaSharp::Register::Module") {
                        ["ModuleId"] = FnRef("AWS::StackName"),
                        ["ModuleName"] = _builder.Name,
                        ["ModuleVersion"] = _builder.Version.ToString()
                    },
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: null
                );
            }

            // create module IAM role used by all functions
            var functions = _builder.Entries.OfType<FunctionEntry>().ToList();
            if(functions.Any()) {

                // create module role
                _builder.AddResource(
                    parent: moduleEntry,
                    name: "Role",
                    description: null,
                    scope: null,
                    resource: new Humidifier.IAM.Role {
                        AssumeRolePolicyDocument = new Humidifier.PolicyDocument {
                            Version = "2012-10-17",
                            Statement = new[] {
                                new Humidifier.Statement {
                                    Sid = "ModuleLambdaInvocation",
                                    Effect = "Allow",
                                    Principal = new Humidifier.Principal {
                                        Service = "lambda.amazonaws.com"
                                    },
                                    Action = "sts:AssumeRole"
                                }
                            }.ToList()
                        },
                        Policies = new[] {
                            new Humidifier.IAM.Policy {
                                PolicyName = FnSub("${AWS::StackName}ModulePolicy"),
                                PolicyDocument = new Humidifier.PolicyDocument {
                                    Version = "2012-10-17",
                                    Statement = new List<Humidifier.Statement>()
                                }
                            }
                        }.ToList()
                    },
                    resourceArnAttribute: null,
                    dependsOn: null,
                    condition: null
                );

                // permission needed for writing to log streams (but not for creating log groups!)
                _builder.AddGrant(
                    sid: "ModuleLogStreamAccess",
                    awsType: null,
                    reference: "arn:aws:logs:*:*:*",
                    allow: new[] {
                        "logs:CreateLogStream",
                        "logs:PutLogEvents"
                    }
                );

                // permissions needed for lambda functions to exist in a VPC
                if(functions.Any(function => function.Function.VpcConfig != null)) {
                    _builder.AddGrant(
                        sid: "ModuleVpcNetworkInterfaces",
                        awsType: null,
                        reference: "*",
                        allow: new[] {
                            "ec2:DescribeNetworkInterfaces",
                            "ec2:CreateNetworkInterface",
                            "ec2:DeleteNetworkInterface"
                        }
                    );
                }

                // create function registration
                if(_builder.HasModuleRegistration && _builder.HasLambdaSharpDependencies) {

                    // create CloudWatch Logs IAM role to invoke kinesis stream
                     _builder.AddResource(
                        parent: moduleEntry,
                        name: "CloudWatchLogsRole",
                        description: null,
                        scope: null,
                        resource:  new Humidifier.IAM.Role {
                            AssumeRolePolicyDocument = new Humidifier.PolicyDocument {
                                Version = "2012-10-17",
                                Statement = new[] {
                                    new Humidifier.Statement {
                                        Sid = "CloudWatchLogsKinesisInvocation",
                                        Effect = "Allow",
                                        Principal = new Humidifier.Principal {
                                            Service = FnSub("logs.${AWS::Region}.amazonaws.com")
                                        },
                                        Action = "sts:AssumeRole"
                                    }
                                }.ToList()
                            },
                            Policies = new[] {
                                new Humidifier.IAM.Policy {
                                    PolicyName = FnSub("${AWS::StackName}ModuleCloudWatchLogsPolicy"),
                                    PolicyDocument = new Humidifier.PolicyDocument {
                                        Version = "2012-10-17",
                                        Statement = new[] {
                                            new Humidifier.Statement {
                                                Sid = "CloudWatchLogsKinesisPermissions",
                                                Effect = "Allow",
                                                Action = "kinesis:PutRecord",
                                                Resource = FnRef("Module::LoggingStreamArn")
                                            }
                                        }.ToList()
                                    }
                                }
                            }.ToList()
                        },
                        resourceArnAttribute: null,
                        dependsOn: null,
                        condition: null
                    );
                }
            }
        }
    }
}