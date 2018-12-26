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
using System.Linq;
using MindTouch.LambdaSharp.Tool.Model;

namespace MindTouch.LambdaSharp.Tool.Cli.Build {
    using static ModelFunctions;

    public class ModelPostLinkerValidation : AModelProcessor {

        //--- Fields ---
        private ModuleBuilder _builder;

        //--- Constructors ---
        public ModelPostLinkerValidation(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Validate(ModuleBuilder builder) {
            _builder = builder;
            AtLocation("Entries", () => {
                foreach(var entry in builder.Entries) {
                    AtLocation(entry.FullName, () => {
                        switch(entry) {
                        case FunctionEntry functionEntry:
                            ValidateFunction(functionEntry);
                            break;
                        case ResourceEntry resourceEntry:
                            switch(resourceEntry.Resource) {
                            case Humidifier.CloudFormation.Macro macro:
                                ValidateFunction((object)macro.FunctionName);
                                break;
                            }
                            break;
                        }
                    });
                }
            });
            AtLocation("Outputs", () => {
                foreach(var output in builder.Outputs) {
                    switch(output) {
                    case CustomResourceHandlerOutput customResourceHandlerOutput:
                        AtLocation(customResourceHandlerOutput.CustomResourceType, () => {
                            ValidateHandler(customResourceHandlerOutput.Handler);
                        });
                        break;
                    }
                }
            });
        }

        public void ValidateFunction(FunctionEntry function) {
            var index = 0;
            foreach(var source in function.Sources) {
                AtLocation($"{++index}", () => {
                    switch(source) {
                    case TopicSource topicSource:
                        ValidateSourceParameter(topicSource.TopicName, "AWS::SNS::Topic");
                        break;
                    case ScheduleSource scheduleSource:

                        // no references to validate
                        break;
                    case ApiGatewaySource apiGatewaySource:

                        // no references to validate
                        break;
                    case S3Source s3Source:
                        ValidateSourceParameter(s3Source.Bucket, "AWS::S3::Bucket");
                        break;
                    case SqsSource sqsSource:
                        ValidateSourceParameter(sqsSource.Queue, "AWS::SQS::Queue");
                        break;
                    case AlexaSource alexaSource:
                        break;
                    case DynamoDBSource dynamoDBSource:
                        ValidateSourceParameter(dynamoDBSource.DynamoDB, "AWS::DynamoDB::Table");
                        break;
                    case KinesisSource kinesisSource:
                        ValidateSourceParameter(kinesisSource.Kinesis, "AWS::Kinesis::Stream");
                        break;
                    }
                });
            }
        }

        private void ValidateSourceParameter(object value, string awsType) {
            if(value is string literalValue) {
                ValidateSourceParameter(literalValue);
            } else if(TryGetFnRef(value, out string refKey)) {
                ValidateSourceParameter(refKey);
            } else {
                AddError("invalid expression");
            }

            // local functions
            void ValidateSourceParameter(string fullName) {
                if(!_builder.TryGetEntry(fullName, out AModuleEntry entry)) {
                    AddError($"could not find function source {fullName}");
                    return;
                }
                switch(entry) {
                case VariableEntry _:
                case InputEntry _:
                case PackageEntry _:
                case ResourceEntry _:
                case FunctionEntry _:
                    if(awsType != entry.Type) {
                        AddError($"function source '{fullName}' must be {awsType}, but was {entry.Type}");
                    }
                    break;
                case ConditionEntry _:
                    AddError($"function source '{fullName}' cannot be a condition '{entry.FullName}'");
                    break;
                case MappingEntry _:
                    AddError($"function source '{fullName}' cannot be a mapping '{entry.FullName}'");
                    break;
                default:
                    throw new ApplicationException($"unexpected entry type: {entry.GetType()}");
                }
            }
        }

        private void ValidateHandler(object handler) {
            if(!(handler is string fullName) && !TryGetFnRef(handler, out fullName)) {
                AddError("invalid expression");
                return;
            }
            if(!_builder.TryGetEntry(fullName, out AModuleEntry entry)) {
                AddError($"could not find handler entry {fullName}");
                return;
            }
            switch(entry) {
            case VariableEntry _:
            case InputEntry _:
            case PackageEntry _:
            case ResourceEntry _:
            case FunctionEntry _:
                if((entry.Type != "AWS::Lambda::Function") && (entry.Type != "AWS::SNS::Topic")) {
                    AddError($"handler reference '{fullName}' must be either be AWS::SNS::Topic or AWS::Lambda::Function, but was {entry.Type}");
                }
                break;
            case ConditionEntry _:
                AddError($"handler reference '{fullName}' cannot be a condition '{entry.FullName}'");
                break;
            case MappingEntry _:
                AddError($"handler reference '{fullName}' cannot be a mapping '{entry.FullName}'");
                break;
            default:
                throw new ApplicationException($"unexpected entry type: {entry.GetType()}");
            }
        }

        private void ValidateFunction(object functionName) {
            if(!(functionName is string fullName) && !TryGetFnRef(functionName, out fullName)) {
                AddError("invalid expression");
                return;
            }
            if(!_builder.TryGetEntry(fullName, out AModuleEntry entry)) {
                AddError($"could not find function entry {fullName}");
                return;
            }
            switch(entry) {
            case VariableEntry _:
            case InputEntry _:
            case PackageEntry _:
            case ResourceEntry _:
            case FunctionEntry _:
                if(entry.Type != "AWS::Lambda::Function") {
                    AddError($"function reference '{fullName}' must be be AWS::Lambda::Function, but was {entry.Type}");
                }
                break;
            case ConditionEntry _:
                AddError($"function reference '{fullName}' cannot be a condition '{entry.FullName}'");
                break;
            case MappingEntry _:
                AddError($"function reference '{fullName}' cannot be a mapping '{entry.FullName}'");
                break;
            default:
                throw new ApplicationException($"unexpected entry type: {entry.GetType()}");
            }
        }
    }
}