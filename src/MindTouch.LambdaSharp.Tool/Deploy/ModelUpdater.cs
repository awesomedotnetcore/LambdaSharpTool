/*
 * MindTouch λ#
 * Copyright (C) 2006-2018 MindTouch, Inc.
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
using System.Threading.Tasks;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.S3.Transfer;
using MindTouch.LambdaSharp.Tool.Model;
using MindTouch.LambdaSharp.Tool.Internal;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool.Deploy {
    using CloudFormationStack = Amazon.CloudFormation.Model.Stack;
    using CloudFormationParameter = Amazon.CloudFormation.Model.Parameter;

    public class ModelUpdater : AModelProcessor {

        //--- Class Fields ---
        private static HashSet<string> _protectedResourceTypes = new HashSet<string> {
            "AWS::ApiGateway::RestApi",
            "AWS::AppSync::GraphQLApi",
            "AWS::DynamoDB::Table",
            "AWS::EC2::Instance",
            "AWS::EMR::Cluster",
            "AWS::Kinesis::Stream",
            "AWS::KinesisFirehose::DeliveryStream",
            "AWS::KMS::Key",
            "AWS::Neptune::DBCluster",
            "AWS::Neptune::DBInstance",
            "AWS::RDS::DBInstance",
            "AWS::Redshift::Cluster",
            "AWS::S3::Bucket"
        };

        //--- Constructors ---
        public ModelUpdater(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public async Task<bool> DeployChangeSetAsync(
            ModuleManifest manifest,
            ModuleLocation cloudformation,
            string stackName,
            bool allowDataLoss,
            bool protectStack,
            Dictionary<string, string> inputs,
            bool forceDeploy
        ) {
            var now = DateTime.UtcNow;

            // check if cloudformation stack already exists and is in a final state
            var mostRecentStackEventId = await Settings.CfClient.GetMostRecentStackEventIdAsync(stackName);
            Console.WriteLine($"Deploying stack: {stackName} [{cloudformation.ModuleName}]");

            // set optional notification topics for cloudformation operations
            var notificationArns =  new List<string>();
            if(Settings.DeploymentNotificationsTopicArn != null) {
                notificationArns.Add(Settings.DeploymentNotificationsTopicArn);
            }

            // initialize template parameters
            var parameters = new List<CloudFormationParameter> {
                new CloudFormationParameter {
                    ParameterKey = "DeploymentPrefix",
                    ParameterValue = string.IsNullOrEmpty(Settings.Tier) ? "" : Settings.Tier + "-"
                },
                new CloudFormationParameter {
                    ParameterKey = "DeploymentPrefixLowercase",
                    ParameterValue = string.IsNullOrEmpty(Settings.Tier) ? "" : Settings.Tier.ToLowerInvariant() + "-"
                },
                new CloudFormationParameter {
                    ParameterKey = "DeploymentBucketName",
                    ParameterValue = cloudformation.BucketName ?? ""
                }
            };
            foreach(var input in inputs) {
                parameters.Add(new CloudFormationParameter {
                    ParameterKey = input.Key,
                    ParameterValue = input.Value
                });
            }

            // create change-set
            var success = false;
            var changeSetName = $"{cloudformation.ModuleName}-{now:yyyy-MM-dd-hh-mm-ss}";
            var templateUrl = $"https://{cloudformation.BucketName}.s3.amazonaws.com/{cloudformation.TemplatePath}";
            var updateOrCreate = (mostRecentStackEventId != null) ? "update" : "create";
            Console.WriteLine($"=> Stack {updateOrCreate} initiated for {stackName}");
            var response = await Settings.CfClient.CreateChangeSetAsync(new CreateChangeSetRequest {
                Capabilities = new List<string> {
                    "CAPABILITY_NAMED_IAM"
                },
                ChangeSetName = changeSetName,
                ChangeSetType = (mostRecentStackEventId != null) ? ChangeSetType.UPDATE : ChangeSetType.CREATE,
                Description = $"Stack {updateOrCreate} {cloudformation.ModuleName} (v{cloudformation.ModuleVersion})",
                NotificationARNs = notificationArns,
                Parameters = parameters,
                StackName = stackName,
                TemplateURL = templateUrl
            });
            try {
                var changes = await WaitForChangeSetAsync(response.Id);
                if(changes == null) {
                    return false;
                }
                if(!changes.Any()) {
                    Console.WriteLine("=> No stack update required");
                    return true;
                }

                //  changes
                if(!allowDataLoss) {
                    var lossyChanges = DetectLossyChanges(changes);
                    if(lossyChanges.Any()) {
                        AddError("one or more resources could be replaced or deleted; use --allow-data-loss to proceed");
                        Console.WriteLine("=> WARNING: Detected potential data-loss in the following resources");
                        foreach(var lossy in lossyChanges) {
                            Console.WriteLine($"{lossy.ResourceChange.Replacement,-11} {lossy.ResourceChange.ResourceType,-55} {TranslateToFullName(lossy.ResourceChange.LogicalResourceId)}");
                        }
                        return false;
                    }
                }

                // execute change-set
                await Settings.CfClient.ExecuteChangeSetAsync(new ExecuteChangeSetRequest {
                    ChangeSetName = changeSetName,
                    StackName = stackName
                });
                var outcome = await Settings.CfClient.TrackStackUpdateAsync(stackName, mostRecentStackEventId, manifest.ResourceFullNames);
                if(outcome.Success) {
                    Console.WriteLine($"=> Stack {updateOrCreate} finished");
                    ShowStackResult(outcome.Stack);
                    success = true;
                } else {
                    Console.WriteLine($"=> Stack {updateOrCreate} FAILED");
                }

                // optionally enable stack protection
                if(success) {

                    // on success, protect the stack if requested
                    if(protectStack) {
                        await Settings.CfClient.UpdateTerminationProtectionAsync(new UpdateTerminationProtectionRequest {
                            EnableTerminationProtection = protectStack,
                            StackName = stackName
                        });
                    }
                } else if(mostRecentStackEventId == null) {

                    // delete a new stack that failed to create
                    try {
                        await Settings.CfClient.DeleteStackAsync(new DeleteStackRequest {
                            StackName = stackName
                        });
                     } catch { }
                }
                return success;
            } finally {
                try {
                    await Settings.CfClient.DeleteChangeSetAsync(new DeleteChangeSetRequest {
                        ChangeSetName = response.Id
                    });
                } catch { }
            }

            // local function
            string TranslateToFullName(string logicalId) {
                var fullName = logicalId;
                manifest.ResourceFullNames?.TryGetValue(logicalId, out fullName);
                return fullName ?? logicalId;
            }
        }

        private void ShowStackResult(CloudFormationStack stack) {
            var outputs = stack.Outputs;
            if(outputs.Any()) {
                Console.WriteLine("Stack output values:");
                foreach(var output in outputs.OrderBy(output => output.OutputKey)) {
                    Console.WriteLine($"=> {output.Description ?? output.OutputKey}: {output.OutputValue}");
                }
            }
        }

        private async Task<List<Change>> WaitForChangeSetAsync(string changeSetId) {

            // wait until change-set if available
            var changeSetRequest = new DescribeChangeSetRequest {
                ChangeSetName = changeSetId
            };
            var changes = new List<Change>();
            while(true) {
                await Task.Delay(TimeSpan.FromSeconds(3));
                var changeSetResponse = await Settings.CfClient.DescribeChangeSetAsync(changeSetRequest);
                if(changeSetResponse.Status == ChangeSetStatus.CREATE_PENDING) {

                    // wait until the change-set is CREATE_COMPLETE
                    continue;
                }
                if(changeSetResponse.Status == ChangeSetStatus.CREATE_IN_PROGRESS) {

                    // wait until the change-set is CREATE_COMPLETE
                    continue;
                }
                if(changeSetResponse.Status == ChangeSetStatus.CREATE_COMPLETE) {
                    changes.AddRange(changeSetResponse.Changes);
                    if(changeSetResponse.NextToken != null) {
                        changeSetRequest.NextToken = changeSetResponse.NextToken;
                        continue;
                    }
                    return changes;
                }
                if(changeSetResponse.Status == ChangeSetStatus.FAILED) {
                    if(changeSetResponse.StatusReason.StartsWith("The submitted information didn't contain changes.", StringComparison.Ordinal)) {
                        return new List<Change>();
                    }
                    AddError($"change-set failed: {changeSetResponse.StatusReason}");
                    return null;
                }
                AddError($"unexpected change-set status: {changeSetResponse.ExecutionStatus}");
                return null;
            }
        }

        private IEnumerable<Change> DetectLossyChanges(IEnumerable<Change> changes) {
            return changes
                .Where(change => change.Type == ChangeType.Resource)
                .Where(change =>
                    (change.ResourceChange.Action == ChangeAction.Remove)
                    || (
                        (change.ResourceChange.Action == ChangeAction.Modify)
                        && (change.ResourceChange.Replacement != Replacement.False)
                        && (_protectedResourceTypes.Contains(change.ResourceChange.ResourceType))
                    )
                ).ToArray();
        }
    }
}