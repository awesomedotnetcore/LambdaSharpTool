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
using System.IO;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using MindTouch.LambdaSharp;
using MindTouch.LambdaSharp.CustomResource;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace MindTouch.LambdaSharpRegistrar.Register {

    public class RequestProperties {

        //--- Properties ---
        public string Tier { get; set; }
        public string ModuleId { get; set; }
        public string ModuleName { get; set; }
        public string ModuleVersion { get; set; }
        public string FunctionId { get; set; }
        public string FunctionName { get; set; }
        public string FunctionLogGroupName { get; set; }
        public int FunctionMaxMemory { get; set; }
        public int FunctionMaxDuration { get; set; }
        public string FunctionPlatform { get; set; }
        public string FunctionFramework { get; set; }
        public string FunctionLanguage { get; set; }
    }

    public class ResponseProperties {

        //--- Properties ---
        public string Registration { get; set; }
    }

    public class Function : ALambdaCustomResourceFunction<RequestProperties, ResponseProperties> {

        //--- Fields ---
        private RegistrationTable _registrations;

        //--- Methods ---
        public async override Task InitializeAsync(LambdaConfig config) {
            var tableName = config.ReadText("RegistrationTable");
            _registrations = new RegistrationTable(new AmazonDynamoDBClient(), tableName);
        }

        protected override async Task<Response<ResponseProperties>> HandleCreateResourceAsync(Request<RequestProperties> request) {
            var properties = request.ResourceProperties;

            // validate request
            if(properties.Tier != DeploymentTier) {

                // TODO (2018-10-11, bjorg): better exception
                throw new Exception("tier mismatch");
            }

            // determine the kind of registration that is requested
            switch(request.ResourceType) {
            case "Custom::LambdaSharpRegisterModule": {
                    LogInfo($"Adding Module: Id={properties.ModuleId}, Name={properties.ModuleName}, Version={properties.ModuleVersion}");
                    var owner = PopulateOwnerMetaData(properties);
                    await _registrations.PutOwnerMetaDataAsync($"M:{owner.ModuleId}", owner);
                    return Respond($"registration:module:{properties.ModuleId}");
                }
            case "Custom::LambdaSharpRegisterFunction": {
                    LogInfo($"Adding Function: Id={properties.FunctionId}, Name={properties.FunctionName}");
                    var owner = await _registrations.GetOwnerMetaDataAsync($"M:{properties.ModuleId}");
                    owner = PopulateOwnerMetaData(properties, owner);
                    await _registrations.PutOwnerMetaDataAsync($"F:{owner.FunctionId}", owner);
                    return Respond($"registration:function:{properties.FunctionId}");
                }
            default:

                // TODO (2018-10-11, bjorg): better exception
                throw new Exception($"bad resource type: {request.ResourceType}");
            }
        }

        protected override async Task<Response<ResponseProperties>> HandleDeleteResourceAsync(Request<RequestProperties> request) {
            var properties = request.ResourceProperties;
            switch(request.ResourceType) {
            case "Custom::LambdaSharpRegisterModule": {
                    LogInfo($"Removing Module: Id={properties.ModuleId}, Name={properties.ModuleName}, Version={properties.ModuleVersion}");
                    await _registrations.DeleteOwnerMetaDataAsync($"M:{properties.ModuleId}");
                    break;
                }
            case "Custom::LambdaSharpRegisterFunction": {
                    LogInfo($"Removing Function: Id={properties.FunctionId}, Name={properties.FunctionName}, LogGroup={properties.FunctionLogGroupName}");
                    await _registrations.DeleteOwnerMetaDataAsync($"F:{properties.FunctionId}");
                    break;
                }
            default:

                // TODO (2018-10-11, bjorg): better exception
                throw new Exception($"bad resource type: {request.ResourceType}");
            }
            return new Response<ResponseProperties>();
        }

        protected override async Task<Response<ResponseProperties>> HandleUpdateResourceAsync(Request<RequestProperties> request)
            => Respond(request.PhysicalResourceId);

        private Response<ResponseProperties> Respond(string registration)
            => new Response<ResponseProperties> {
                PhysicalResourceId = registration,
                Properties = new ResponseProperties {
                    Registration = registration
                }
            };

        private OwnerMetaData PopulateOwnerMetaData(RequestProperties properties, OwnerMetaData owner = null) {
            if(owner == null) {
                owner = new OwnerMetaData();
            }
            owner.Tier = properties.Tier;
            owner.ModuleId = properties.ModuleId;
            owner.ModuleName = properties.ModuleName;
            owner.ModuleVersion = properties.ModuleVersion;
            owner.FunctionId = properties.FunctionId;
            owner.FunctionName = properties.FunctionName;
            owner.FunctionLogGroupName = properties.FunctionLogGroupName;
            owner.FunctionPlatform = properties.FunctionPlatform;
            owner.FunctionFramework = properties.FunctionFramework;
            owner.FunctionLanguage = properties.FunctionLanguage;
            owner.FunctionMaxMemory = properties.FunctionMaxMemory;
            owner.FunctionMaxDuration = TimeSpan.FromSeconds(properties.FunctionMaxDuration);
            return owner;
        }
    }
}