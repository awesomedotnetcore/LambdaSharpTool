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

namespace MindTouch.LambdaSharp.Tool.Model.AST {

    public class FunctionSourceNode {

        //--- Class Fields ---
        public readonly static Dictionary<string, Func<FunctionSourceNode, bool>> FieldCheckers = new Dictionary<string, Func<FunctionSourceNode, bool>> {
            ["Api"] = source => source.Api != null,
            ["Integration"] = source => source.Integration != null,
            ["OperationName"] = source => source.OperationName != null,
            ["ApiKeyRequired"] = source => source.ApiKeyRequired != null,
            ["Schedule"] = source => source.Schedule != null,
            ["Name"] = source => source.Name != null,
            ["S3"] = source => source.S3 != null,
            ["Events"] = source => source.Events != null,
            ["Prefix"] = source => source.Prefix != null,
            ["Suffix"] = source => source.Suffix != null,
            ["SlackCommand"] = source => source.SlackCommand != null,
            ["Topic"] = source => source.Topic != null,
            ["Sqs"] = source => source.Sqs != null,
            ["BatchSize"] = source => source.BatchSize != null,
            ["Alexa"] = source => source.Alexa != null,
            ["DynamoDB"] = source => source.DynamoDB != null,
            ["StartingPosition"] = source => source.StartingPosition != null,
            ["Kinesis"] = source => source.Kinesis != null
        };

        public static readonly Dictionary<string, IEnumerable<string>> FieldCombinations = new Dictionary<string, IEnumerable<string>> {
            ["Api"] = new[] { "Integration", "OperationName", "ApiKeyRequired" },
            ["Schedule"] = new[] { "Name" },
            ["S3"] = new[] { "Events", "Prefix", "Suffix" },
            ["SlackCommand"] = new string[0],
            ["Topic"] = new string[0],
            ["Sqs"] = new[] { "BatchSize" },
            ["Alexa"] = new string[0],
            ["DynamoDB"] = new[] { "BatchSize", "StartingPosition" },
            ["Kinesis"] = new[] { "BatchSize", "StartingPosition" }
        };

        //--- Properties ---

        // API Gateway Source
        public string Api { get; set; }
        public string Integration { get; set; }
        public string OperationName { get; set; }
        public bool? ApiKeyRequired { get; set; }

        // CloudWatch Schedule Event Source
        public string Schedule { get; set; }
        public string Name { get; set; }

        // S3 Bucket Source
        public string S3 { get; set; }
        public IList<string> Events { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }

        // Slack Command Source
        public string SlackCommand { get; set; }

        // SNS Topic Source
        public string Topic { get; set; }

        // SQS Source
        public string Sqs { get; set; }
        public int? BatchSize { get; set; }

        // Alexa Source
        public object Alexa { get; set; }

        // DynamoDB Source
        public string DynamoDB { get; set; }
        // int? BatchSize { get; set; }
        public string StartingPosition { get; set; }

        // Kinesis Source
        public string Kinesis { get; set; }
        // int? BatchSize { get; set; }
        // string StartingPosition { get; set; }
   }
}