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
using System.Reflection;
using System.Text;
using Humidifier;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MindTouch.LambdaSharp.Tool.Model {
    using static ModelFunctions;

    public static class ResourceMapping {

        //--- Fields ---
        private static readonly IDictionary<string, IDictionary<string, IList<string>>> _iamMappings;
        private static readonly HashSet<string> _supportedCloudFormationTypes;

        //--- Constructors ---
        static ResourceMapping() {

            // read short-hand for IAM mappings from embedded resource
            var assembly = typeof(ResourceMapping).Assembly;
            using(var resource = assembly.GetManifestResourceStream("MindTouch.LambdaSharp.Tool.Resources.IAM-Mappings.yml"))
            using(var reader = new StreamReader(resource, Encoding.UTF8)) {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(new NullNamingConvention())
                    .Build();
                _iamMappings = deserializer.Deserialize<IDictionary<string, IDictionary<string, IList<string>>>>(reader);
            }

            // create list of natively supported CloudFormation types
            _supportedCloudFormationTypes = new HashSet<string> {
                "String",
                "Number",
                "List<Number>",
                "CommaDelimitedList",
                "AWS::SSM::Parameter::Name",
                "AWS::SSM::Parameter::Value<String>",
                "AWS::SSM::Parameter::Value<List<String>>",
                "AWS::SSM::Parameter::Value<CommaDelimitedList>"
            };
            var awsTypes = new[] {
                "AWS::EC2::AvailabilityZone::Name",
                "AWS::EC2::Image::Id",
                "AWS::EC2::Instance::Id",
                "AWS::EC2::KeyPair::KeyName",
                "AWS::EC2::SecurityGroup::GroupName",
                "AWS::EC2::SecurityGroup::Id",
                "AWS::EC2::Subnet::Id",
                "AWS::EC2::Volume::Id",
                "AWS::EC2::VPC::Id",
                "AWS::Route53::HostedZone::Id"
            };
            foreach(var awsType in awsTypes) {
                _supportedCloudFormationTypes.Add(awsType);
                _supportedCloudFormationTypes.Add($"List<{awsType}>");
                _supportedCloudFormationTypes.Add($"AWS::SSM::Parameter::Value<{awsType}>");
                _supportedCloudFormationTypes.Add($"AWS::SSM::Parameter::Value<List<{awsType}>>");
            }
        }

        //--- Methods ---
        public static bool TryResolveAllowShorthand(string awsType, string shorthand, out IList<string> allowed) {
            allowed = null;
            return _iamMappings.TryGetValue(awsType, out IDictionary<string, IList<string>> awsTypeShorthands)
                && awsTypeShorthands.TryGetValue(shorthand, out allowed);
        }

        public static object ExpandResourceReference(string awsType, object arnReference) {

            // NOTE: some AWS resources require additional sub-resource reference
            //  to properly apply permissions across the board.

            switch(awsType) {
            case "AWS::S3::Bucket":

                // S3 Bucket resources must be granted permissions on the bucket AND the keys
                return LiftArnReference().SelectMany(reference => new object[] {
                    reference,
                    FnJoin("", new List<object> { reference, "/*" })
                }).ToList();
            case "AWS::DynamoDB::Table":

                // DynamoDB resources must be granted permissions on the table AND the stream
                return LiftArnReference().SelectMany(reference => new object[] {
                    reference,
                    FnJoin("/", new List<object> { reference, "stream/*" }),
                    FnJoin("/", new List<object> { reference, "index/*" })
                }).ToList();
            default:
                return arnReference;
            }

            // local functions
            IList<object> LiftArnReference()
                => (arnReference is IList<object> arnReferences)
                    ? arnReferences
                    : new object[] { arnReference };
        }

        public static bool HasAttribute(string awsType, string attribute)
            => GetHumidifierType(awsType)
                ?.GetNestedType("Attributes")
                ?.GetFields(BindingFlags.Static | BindingFlags.Public)
                ?.Any(field => (field.FieldType == typeof(string)) && ((string)field.GetValue(null) == attribute))
                ?? false;

        public static bool IsResourceTypeSupported(string awsType) => GetHumidifierType(awsType) != null;

        public static Type GetHumidifierType(string awsType) {
            const string AWS_PREFIX = "AWS::";
            if(!awsType.StartsWith(AWS_PREFIX)) {
                return null;
            }
            var typeName = "Humidifier." + awsType.Substring(AWS_PREFIX.Length).Replace("::", ".");
            return typeof(Humidifier.Resource).Assembly.GetType(typeName, throwOnError: false);
        }

        public static string ToCloudFormationType(string type)
            => _supportedCloudFormationTypes.Contains(type) ? type : "String";
    }
}
