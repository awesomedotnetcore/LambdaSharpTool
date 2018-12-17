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
using System.IO;
using System.Text;
using MindTouch.LambdaSharp.Tool.Internal;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Model.AST;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool.Cli.Build {
    using static ModelFunctions;

    public class ModelModuleInitializer : AModelProcessor {

        //--- Class Fields ---
        private static readonly string DecryptSecretFunctionCode;

        //--- Class Constructor ---
        static ModelModuleInitializer() {
            var assembly = typeof(ModelModuleInitializer).Assembly;
            using(var resource = assembly.GetManifestResourceStream($"MindTouch.LambdaSharp.Tool.Resources.DecryptSecretFunction.js"))
            using(var reader = new StreamReader(resource, Encoding.UTF8)) {
                DecryptSecretFunctionCode = reader.ReadToEnd().Replace("\r", "");
            }
        }

        //--- Fields ---
        private ModuleBuilder _builder;

        //--- Constructors ---
        public ModelModuleInitializer(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Initialize(ModuleBuilder builder) {
            _builder = builder;

            // add module variables
            var moduleEntry = _builder.AddVariable(
                parent: null,
                name: "Module",
                description: "Module Variables",
                type: "String",
                scope: null,
                value: "",
                encryptionContext: null
            );
            _builder.AddVariable(
                parent: moduleEntry,
                name: "Id",
                description: "Module ID",
                type: "String",
                scope: null,
                value: FnRef("AWS::StackName"),
                encryptionContext: null
            );
            _builder.AddVariable(
                parent: moduleEntry,
                name: "Name",
                description: "Module Name",
                type: "String",
                scope: null,
                value: _builder.Name,
                encryptionContext: null
            );
            _builder.AddVariable(
                parent: moduleEntry,
                name: "Version",
                description: "Module Version",
                type: "String",
                scope: null,
                value: _builder.Version.ToString(),
                encryptionContext: null
            );

            // add LambdaSharp Module Options
            var section = "LambdaSharp Module Options";
            _builder.AddParameter(
                parent: null,
                name: "Secrets",
                section: section,
                label: "Secret Keys (ARNs)",
                description: "Comma-separated list of optional secret keys",

                // TODO: see if we can use CommaDelimitedList here instead?
                type: "String",
                scope: null,
                noEcho: null,
                defaultValue: "",
                constraintDescription: null,
                allowedPattern: null,
                allowedValues: null,
                maxLength: null,
                maxValue: null,
                minLength: null,
                minValue: null,
                allow: null,
                properties: null,
                arnAttribute: null,
                encryptionContext: null,
                pragmas: null
            );
            _builder.AddCondition("SecretsIsEmpty", FnEquals(FnRef("Secrets"), ""));

            // add standard parameters (unless requested otherwise)
            if(_builder.HasLambdaSharpDependencies) {

                // add LambdaSharp Module Internal resource imports
                var lambdasharp = _builder.AddImport(
                    import: "LambdaSharp",
                    description: "LambdaSharp Dependencies"
                );
                _builder.AddParameter(
                    parent: lambdasharp,
                    name: "DeadLetterQueueArn",
                    section: null,
                    label: "Dead Letter Queue (ARN)",
                    description: "Dead letter queue for functions",

                    // TODO (2018-12-01, bjorg): consider using 'AWS::SQS::Queue'
                    type: "String",
                    scope: null,
                    noEcho: null,
                    defaultValue: null,
                    constraintDescription: null,
                    allowedPattern: null,
                    allowedValues: null,
                    maxLength: null,
                    maxValue: null,
                    minLength: null,
                    minValue: null,
                    allow: null /* new[] {
                        "sqs:SendMessage"
                    }*/,
                    properties: null,
                    arnAttribute: null,
                    encryptionContext: null,
                    pragmas: null
                );
                _builder.AddParameter(
                    parent: lambdasharp,
                    name: "LoggingStreamArn",
                    section: null,
                    label: "Logging Stream (ARN)",
                    description: "Logging kinesis stream for functions",

                    // NOTE (2018-12-11, bjorg): we use type 'String' to be more flexible with the type of values we're willing to take
                    type: "String",
                    scope: null,
                    noEcho: null,
                    defaultValue: null,
                    constraintDescription: null,
                    allowedPattern: null,
                    allowedValues: null,
                    maxLength: null,
                    maxValue: null,
                    minLength: null,
                    minValue: null,
                    allow: null,
                    properties: null,
                    arnAttribute: null,
                    encryptionContext: null,
                    pragmas: null
                );
                _builder.AddParameter(
                    parent: lambdasharp,
                    name: "DefaultSecretKeyArn",
                    section: null,
                    label: "Secret Key (ARN)",
                    description: "Default secret key for functions",

                    // TODO (2018-12-01, bjorg): consider using 'AWS::KMS::Key'
                    type: "String",
                    scope: null,
                    noEcho: null,
                    defaultValue: null,
                    constraintDescription: null,
                    allowedPattern: null,
                    allowedValues: null,
                    maxLength: null,
                    maxValue: null,
                    minLength: null,
                    minValue: null,

                    // NOTE (2018-12-11, bjorg): we grant decryption access later as part of a bulk permissioning operation
                    allow: null,
                    properties: null,
                    arnAttribute: null,
                    encryptionContext: null,
                    pragmas: null
                );
                _builder.AddSecret(FnRef("Module::DefaultSecretKeyArn"));

                // add lambdasharp imports
                _builder.AddVariable(
                    parent: moduleEntry,
                    name: "DeadLetterQueueArn",
                    description: "Module Dead Letter Queue (ARN)",
                    type: "String",
                    scope: null,
                    value: FnRef("LambdaSharp::DeadLetterQueueArn"),
                    encryptionContext: null
                );
                _builder.AddVariable(
                    parent: moduleEntry,
                    name: "LoggingStreamArn",
                    description: "Module Logging Stream (ARN)",
                    type: "String",
                    scope: null,
                    value: FnRef("LambdaSharp::LoggingStreamArn"),
                    encryptionContext: null
                );
                _builder.AddVariable(
                    parent: moduleEntry,
                    name: "DefaultSecretKeyArn",
                    description: "Module Default Secret Key (ARN)",
                    type: "String",
                    scope: null,
                    value: FnRef("LambdaSharp::DefaultSecretKeyArn"),
                    encryptionContext: null
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

            // add decryption function for secret parameters and values
            var decryptSecretFunction = _builder.AddInlineFunction(
                parent: moduleEntry,
                name: "DecryptSecretFunction",
                description: "Module secret decryption function",
                environment: null,
                sources: null,
                pragmas: null,
                timeout: "30",
                reservedConcurrency: null,
                memory: "128",
                subnets: null,
                securityGroups: null,
                code: DecryptSecretFunctionCode
            );
            decryptSecretFunction.DiscardIfNotReachable = true;

            // add LambdaSharp Deployment Settings
            section = "LambdaSharp Deployment Settings (DO NOT MODIFY)";
            _builder.AddParameter(
                parent: null,
                name: "DeploymentBucketName",
                section: section,
                label: "Deployment S3 Bucket",
                description: "Source deployment S3 bucket name",
                type: "String",
                scope: null,
                noEcho: null,
                defaultValue: null,
                constraintDescription: null,
                allowedPattern: null,
                allowedValues: null,
                maxLength: null,
                maxValue: null,
                minLength: null,
                minValue: null,
                allow: null,
                properties: null,
                arnAttribute: null,
                encryptionContext: null,
                pragmas: null
            );
            _builder.AddParameter(
                parent: null,
                name: "DeploymentPrefix",
                section: section,
                label: "Deployment Prefix",
                description: "Module deployment prefix",
                type: "String",
                scope: null,
                noEcho: null,
                defaultValue: null,
                constraintDescription: null,
                allowedPattern: null,
                allowedValues: null,
                maxLength: null,
                maxValue: null,
                minLength: null,
                minValue: null,
                allow: null,
                properties: null,
                arnAttribute: null,
                encryptionContext: null,
                pragmas: null
            );
            _builder.AddParameter(
                parent: null,
                name: "DeploymentPrefixLowercase",
                section: section,
                label: "Deployment Prefix (lowercase)",
                description: "Module deployment prefix (lowercase)",
                type: "String",
                scope: null,
                noEcho: null,
                defaultValue: null,
                constraintDescription: null,
                allowedPattern: null,
                allowedValues: null,
                maxLength: null,
                maxValue: null,
                minLength: null,
                minValue: null,
                allow: null,
                properties: null,
                arnAttribute: null,
                encryptionContext: null,
                pragmas: null
            );
            _builder.AddParameter(
                parent: null,
                name: "DeploymentParent",
                section: section,
                label: "Parent Stack Name",
                description: "Parent stack name for nested deployments, blank otherwise",
                type: "String",
                scope: null,
                noEcho: null,
                defaultValue: "",
                constraintDescription: null,
                allowedPattern: null,
                allowedValues: null,
                maxLength: null,
                maxValue: null,
                minLength: null,
                minValue: null,
                allow: null,
                properties: null,
                arnAttribute: null,
                encryptionContext: null,
                pragmas: null
            );
            _builder.AddParameter(
                parent: null,
                name: "DeploymentChecksum",
                section: section,
                label: "Deployment Checksum",
                description: "CloudFormation template MD5 checksum",
                type: "String",
                scope: null,
                noEcho: null,
                defaultValue: "",
                constraintDescription: null,
                allowedPattern: null,
                allowedValues: null,
                maxLength: null,
                maxValue: null,
                minLength: null,
                minValue: null,
                allow: null,
                properties: null,
                arnAttribute: null,
                encryptionContext: null,
                pragmas: null
            );

            // create module IAM role used by all functions
            var moduleRole = _builder.AddResource(
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
                condition: null,
                pragmas: null
            );
            moduleRole.DiscardIfNotReachable = true;

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
            var functions = _builder.Entries.OfType<FunctionEntry>().ToList();

            // check if lambdasharp specific resources need to be initialized
            if(_builder.HasLambdaSharpDependencies) {
                foreach(var function in functions) {

                    // initialize dead-letter queue
                    function.Function.DeadLetterConfig = new Humidifier.Lambda.FunctionTypes.DeadLetterConfig {
                        TargetArn = FnRef("Module::DeadLetterQueueArn")
                    };
                }
            }

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

            // add module registration
            if(_builder.HasModuleRegistration) {
                _builder.AddDependency("LambdaSharpRegistrar", Settings.ToolVersion, maxVersion: null, bucketName: null);

                // create module registration
                _builder.AddResource(
                    parent: moduleEntry,
                    name: "Registration",
                    description: null,
                    type: "LambdaSharp::Register::Module",
                    scope: null,
                    value: null,
                    allow: null,
                    properties: new Dictionary<string, object> {
                        ["ModuleId"] = FnRef("AWS::StackName"),
                        ["ModuleName"] = _builder.Name,
                        ["ModuleVersion"] = _builder.Version.ToString()
                    },
                    dependsOn: null,
                    arnAttribute: null,
                    condition: null,
                    pragmas: null
                );

                // handle function registrations
                var registeredFunctions = _builder.Entries
                    .OfType<FunctionEntry>()
                    .Where(function => function.HasFunctionRegistration)
                    .ToList();
                if(registeredFunctions.Any()) {

                    // create CloudWatch Logs IAM role to invoke kinesis stream
                    if(_builder.HasLambdaSharpDependencies) {
                        var cloudWatchLogRole = _builder.AddResource(
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
                            condition: null,
                            pragmas: null
                        );
                        cloudWatchLogRole.DiscardIfNotReachable = true;
                    }

                    // create registration-related resources for functions
                    foreach(var function in registeredFunctions) {

                        // create function registration
                        _builder.AddResource(
                            parent: function,
                            name: "Registration",
                            description: null,
                            type: "LambdaSharp::Register::Function",
                            scope: null,
                            value: null,
                            allow: null,
                            properties: new Dictionary<string, object> {
                                ["ModuleId"] = FnRef("AWS::StackName"),
                                ["FunctionId"] = FnRef(function.ResourceName),
                                ["FunctionName"] = function.Name,
                                ["FunctionLogGroupName"] = FnSub($"/aws/lambda/${{{function.ResourceName}}}"),
                                ["FunctionPlatform"] = "AWS Lambda",
                                ["FunctionFramework"] = function.Function.Runtime,
                                ["FunctionLanguage"] = function.Language,
                                ["FunctionMaxMemory"] = function.Function.MemorySize,
                                ["FunctionMaxDuration"] = function.Function.Timeout
                            },
                            dependsOn: new[] { "Module::Registration" },
                            arnAttribute: null,
                            condition: null,
                            pragmas: null
                        );

                        // create function log-group subscription
                        if(_builder.HasLambdaSharpDependencies) {
                            _builder.AddResource(
                                parent: function,
                                name: "LogGroupSubscription",
                                description: null,
                                scope: null,
                                resource: new Humidifier.Logs.SubscriptionFilter {
                                    DestinationArn = FnRef("Module::LoggingStreamArn"),
                                    FilterPattern = "-\"*** \"",
                                    LogGroupName = FnRef($"{function.FullName}::LogGroup"),
                                    RoleArn = FnGetAtt("Module::CloudWatchLogsRole", "Arn")
                                },
                                resourceArnAttribute: null,
                                dependsOn: null,
                                condition: null,
                                pragmas: null
                            );
                        }
                    }
                }
            }
        }
    }
}