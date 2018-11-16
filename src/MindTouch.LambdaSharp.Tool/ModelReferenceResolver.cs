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
using System.Linq;
using System.Text.RegularExpressions;
using Humidifier;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelReferenceResolver : AModelProcessor {

        //--- Constants ---
        private const string SUBVARIABLE_PATTERN = @"\$\{(?!\!)[^\}]+\}";

        //--- Class Methods ---
        private static void DebugWriteLine(string format) {
#if true
            Console.WriteLine(format);
#endif
        }

        //--- Constructors ---
        public ModelReferenceResolver(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Resolve(Module module) {

            // resolve scopes
            var functionNames = new HashSet<string>(module.Resources.OfType<Function>().Select(function => function.FullName));
            foreach(var variable in module.Variables.Values) {
                if(variable.Scope.Contains("*")) {
                    variable.Scope = variable.Scope
                        .Where(scope => scope != "*")
                        .Union(functionNames)
                        .Distinct()
                        .OrderBy(item => item)
                        .ToList();
                }
            }

            // resolve outputs
            foreach(var output in module.Outputs.OfType<ExportOutput>()) {
                if(output.Value == null) {

                    // NOTE: if no value is provided, we expect the export name to correspond to a
                    //  parameter name; if it does, we export the ARN value of that parameter; in
                    //  addition, we assume its description if none is provided.

                    var parameter = module.Resources.FirstOrDefault(p => p.Name == output.Name);
                    if(parameter == null) {
                        AddError("could not find matching variable");
                        output.Value = "<BAD>";
                    } else if(parameter is InputParameter) {

                        // input parameters are always expected to be in ARN format
                        output.Value = FnRef(parameter.Name);
                    } else {
                        output.Value = ResourceMapping.GetArnReference((parameter as AResourceParameter)?.Resource?.Type, parameter.FullName);
                    }

                    // only set the description if the value was not set
                    if(output.Description == null) {
                        output.Description = parameter.Description;
                    }
                }
            }

            // resolve all inter-variable references
            var freeVariables = new Dictionary<string, ModuleVariable>();
            var boundVariables = new Dictionary<string, ModuleVariable>();
            DiscoverVariables();
            ResolveVariables();
            ReportUnresolvedVariables();
            if(Settings.HasErrors) {
                return;
            }

            // resolve references in resource properties
            foreach(var parameter in module.GetAllResources()
                .OfType<AResourceParameter>()
                .Where(p => p.Resource?.Properties != null)
            ) {
                parameter.Resource.Properties = (IDictionary<string, object>)Substitute(parameter.Resource.Properties, ReportMissingReference);
            }

            // resolve references in output values
            foreach(var output in module.Outputs) {
                switch(output) {
                case ExportOutput exportOutput:
                    exportOutput.Value = ResolveResourceNamesToLogicalIds(Substitute(exportOutput.Value, ReportMissingReference));
                    break;
                case CustomResourceHandlerOutput _:
                case MacroOutput _:

                    // nothing to do
                    break;
                default:
                    throw new InvalidOperationException($"cannot resolve references for this type: {output?.GetType()}");
                }
            }

            // resolve references in functions
            foreach(var function in module.Resources.OfType<Function>()) {
                function.Environment = (IDictionary<string, object>)ResolveResourceNamesToLogicalIds(Substitute(function.Environment, ReportMissingReference));

                // update VPC information
                if(function.VPC != null) {
                    function.VPC.SecurityGroupIds = ResolveResourceNamesToLogicalIds(Substitute(function.VPC.SecurityGroupIds));
                    function.VPC.SubnetIds = ResolveResourceNamesToLogicalIds(Substitute(function.VPC.SubnetIds));
                }

                // update function sources
                foreach(var source in function.Sources) {
                    switch(source) {
                    case AlexaSource alexaSource:
                        if(alexaSource.EventSourceToken != null) {
                            alexaSource.EventSourceToken = ResolveResourceNamesToLogicalIds(Substitute(alexaSource.EventSourceToken, ReportMissingReference));
                        }
                        break;
                    }

                }
            }

            // resolve everything to logical ids
            module.Secrets = module.Secrets.Select(ResolveResourceNamesToLogicalIds).ToList();
            module.Conditions = (IDictionary<string, object>)ResolveResourceNamesToLogicalIds(module.Conditions);
            foreach(var parameter in module.GetAllResources()) {
                if((parameter is AResourceParameter resourceParameter) && (resourceParameter.Resource != null)) {
                    var resource = resourceParameter.Resource;
                    if(resource.Properties?.Any() == true) {
                        resource.Properties = (IDictionary<string, object>)ResolveResourceNamesToLogicalIds(resource.Properties);
                    }
                    if(resource.DependsOn?.Any() == true) {
                        resourceParameter.Resource.DependsOn = resourceParameter.Resource.DependsOn.Select(dependency => module.GetResource(dependency).LogicalId).ToList();
                    }
                }
            }
            foreach(var grant in module.Grants) {
                grant.References = ResolveResourceNamesToLogicalIds(grant.References);
            }

            // local functions
            void DiscoverVariables() {
                foreach(var variable in module.Variables) {
                    switch(variable.Value.Reference) {
                    case null:
                        throw new ApplicationException($"variable cannot be null: {variable.Key}");
                    case string _:
                        freeVariables[variable.Key] = variable.Value;
                        DebugWriteLine($"FREE => {variable.Key}");
                        break;
                    case IList<object> list:
                        if(list.All(value => value is string)) {
                            freeVariables[variable.Key] = variable.Value;
                            DebugWriteLine($"FREE => {variable.Key}");
                        } else {
                            boundVariables[variable.Key] = variable.Value;
                            DebugWriteLine($"BOUND => {variable.Key}");
                        }
                        break;
                    default:
                        boundVariables[variable.Key] = variable.Value;
                        DebugWriteLine($"BOUND => {variable.Key}");
                        break;
                    }
                }
            }

            void ResolveVariables() {
                bool progress;
                do {
                    progress = false;
                    foreach(var variable in boundVariables.Values.ToList()) {

                        // NOTE (2018-10-04, bjorg): each iteration, we loop over a bound variable;
                        //  in the iteration, we attempt to substitute all references with free variables;
                        //  if we do, the variable can be added to the pool of free variables;
                        //  if we iterate over all bound variables without making progress, then we must have
                        //  a circular dependency and we stop.

                        var doesNotContainBoundVariables = true;
                        variable.Reference = Substitute(variable.Reference, (string missingName) => {
                            doesNotContainBoundVariables = doesNotContainBoundVariables && !boundVariables.ContainsKey(missingName);
                        });
                        if(doesNotContainBoundVariables) {

                            // capture that progress towards resolving all bound variables has been made;
                            // if ever an iteration does not produces progress, we need to stop; otherwise
                            // we will loop forever
                            progress = true;

                            // promote bound variable to free variable
                            freeVariables[variable.FullName] = variable;
                            boundVariables.Remove(variable.FullName);
                            DebugWriteLine($"RESOLVED => {variable.FullName} = {Newtonsoft.Json.JsonConvert.SerializeObject(variable.Reference)}");
                        }
                    }
                } while(progress);
            }

            void ReportUnresolvedVariables() {
                foreach(var variable in module.Variables.Values) {
                    Substitute(variable.Reference, ReportMissingReference);
                }
            }

            void ReportMissingReference(string missingName) {
                if(boundVariables.ContainsKey(missingName)) {
                    AddError($"circular !Ref dependency on '{missingName}'");
                } else {
                    AddError($"could not find !Ref dependency '{missingName}'");
                }
            }

            object Substitute(object value, Action<string> missing = null) {

                // check if we need to convert the dictionary keys to be strings
                if(value is IDictionary<object, object> objectMap) {
                    value = objectMap.ToDictionary(kv => (string)kv.Key, kv => kv.Value);
                }
                switch(value) {
                case IDictionary<string, object> map:
                    map = map.ToDictionary(kv => kv.Key, kv => Substitute(kv.Value, missing));
                    if(map.Count == 1) {

                        // handle !Ref expression
                        if(map.TryGetValue("Ref", out object refObject) && (refObject is string refKey)) {
                            if(TrySubstitute(refKey, null, out object found)) {
                                return found ?? map;
                            }
                            DebugWriteLine($"NOT FOUND => {refKey}");
                            missing?.Invoke(refKey);
                            return map;
                        }

                        // handle !GetAtt expression
                        if(
                            map.TryGetValue("Fn::GetAtt", out object getAttObject)
                            && (getAttObject is IList<object> getAttArgs)
                            && (getAttArgs.Count == 2)
                            && getAttArgs[0] is string getAttKey
                            && getAttArgs[1] is string getAttAttribute
                        ) {
                            if(TrySubstitute(getAttKey, getAttAttribute, out object found)) {
                                return found ?? map;
                            }
                            DebugWriteLine($"NOT FOUND => {getAttKey}");
                            missing?.Invoke(getAttKey);
                            return map;
                        }

                        // handle !Sub expression
                        if(map.TryGetValue("Fn::Sub", out object subObject)) {
                            string subPattern;
                            IDictionary<string, object> subArgs = null;

                            // determine which form of !Sub is being used
                            if(subObject is string) {
                                subPattern = (string)subObject;
                                subArgs = new Dictionary<string, object>();
                            } else if(
                                (subObject is IList<object> subList)
                                && (subList.Count == 2)
                                && (subList[0] is string)
                                && (subList[1] is IDictionary<string, object>)
                            ) {
                                subPattern = (string)subList[0];
                                subArgs = (IDictionary<string, object>)subList[1];
                            } else {
                                return map;
                            }

                            // replace as many ${VAR} occurrences as possible
                            var substitions = false;
                            subPattern = Regex.Replace(subPattern, SUBVARIABLE_PATTERN, match => {
                                var matchText = match.ToString();
                                var name = matchText.Substring(2, matchText.Length - 3).Trim().Split('.', 2);
                                if(!subArgs.ContainsKey(name[0])) {
                                    if(TrySubstitute(name[0], (name.Length == 2) ? name[1] : null, out object found)) {
                                        substitions = true;
                                        if(found == null) {
                                            return matchText;
                                        }
                                        if(found is string text) {
                                            return text;
                                        }
                                        var argName = $"P{subArgs.Count}";
                                        subArgs.Add(argName, found);
                                        return "${" + argName + "}";
                                    }
                                    DebugWriteLine($"NOT FOUND => {name[0]}");
                                    missing?.Invoke(name[0]);
                                }
                                return matchText;
                            });
                            if(!substitions) {
                                return map;
                            }

                            // determine which form of !Sub to construct
                            return subArgs.Any()
                                ? FnSub(subPattern, subArgs)
                                : Regex.IsMatch(subPattern, SUBVARIABLE_PATTERN)
                                ? FnSub(subPattern)
                                : subPattern;
                        }
                    }
                    return map;
                case IList<object> list:
                    return list.Select(item => Substitute(item, missing)).ToList();
                case null:
                    AddError("null value is not allowed");
                    return value;
                default:

                    // nothing further to substitute
                    return value;
                }
            }

            bool TrySubstitute(string key, string attribute, out object found) {
                found = null;
                if(key.StartsWith("AWS::", StringComparison.Ordinal)) {

                    // built-in AWS variable can be kept as-is
                    return true;
                } else if(key.StartsWith("@", StringComparison.Ordinal)) {

                    // module resource names can be kept as-is
                    return true;
                }

                // see if the requested key can be resolved using a free variable
                if(freeVariables.TryGetValue(key, out ModuleVariable freeVariable)) {
                    if(attribute != null) {
                        if(
                            (freeVariable.Reference is IDictionary<string, object> map)
                            && (map.Count == 1)
                            && map.TryGetValue("Ref", out object refObject)
                            && (refObject is string refValue)
                        ) {
                            found = FnGetAtt(refValue, attribute);
                        } else {
                            AddError($"reference '{key}' must resolve to a CloudFormation resource to be used with an Fn::GetAtt expression");
                            found = FnGetAtt(key, attribute);
                        }
                    } else {
                        found = freeVariable.Reference;
                    }
                    return true;
                }
                return false;
            }

            object ResolveResourceNamesToLogicalIds(object value) {
                switch(value) {
                 case IDictionary<string, object> map:
                    map = map.ToDictionary(kv => kv.Key, kv => ResolveResourceNamesToLogicalIds(kv.Value));
                    if(map.Count == 1) {

                        // handle !Ref expression
                        if(map.TryGetValue("Ref", out object refObject) && (refObject is string refKey) && refKey.StartsWith("@", StringComparison.Ordinal)) {
                            return FnRef(refKey.Substring(1));
                        }

                        // handle !GetAtt expression
                        if(
                            map.TryGetValue("Fn::GetAtt", out object getAttObject)
                            && (getAttObject is IList<object> getAttArgs)
                            && (getAttArgs.Count == 2)
                            && getAttArgs[0] is string getAttKey
                            && getAttKey.StartsWith("@", StringComparison.Ordinal)
                            && getAttArgs[1] is string getAttAttribute
                        ) {
                            return FnGetAtt(getAttKey.Substring(1), getAttAttribute);
                        }

                        // handle !Sub expression
                        if(map.TryGetValue("Fn::Sub", out object subObject)) {
                            string subPattern;
                            IDictionary<string, object> subArgs = null;

                            // determine which form of !Sub is being used
                            if(subObject is string) {
                                subPattern = (string)subObject;
                                subArgs = new Dictionary<string, object>();
                            } else if(
                                (subObject is IList<object> subList)
                                && (subList.Count == 2)
                                && (subList[0] is string)
                                && (subList[1] is IDictionary<string, object>)
                            ) {
                                subPattern = (string)subList[0];
                                subArgs = (IDictionary<string, object>)subList[1];
                            } else {
                                return map;
                            }

                            // replace as many ${VAR} occurrences as possible
                            var substitions = false;
                            subPattern = Regex.Replace(subPattern, SUBVARIABLE_PATTERN, match => {
                                var matchText = match.ToString();
                                var name = matchText.Substring(2, matchText.Length - 3).Trim().Split('.', 2);
                                if(!subArgs.ContainsKey(name[0]) && name[0].StartsWith("@", StringComparison.Ordinal)) {
                                    return (name.Length == 2)
                                        ? name[0].Substring(1)
                                        : name[0].Substring(1) + "." + name[1];
                                }
                                return matchText;
                            });
                            if(!substitions) {
                                return map;
                            }

                            // determine which form of !Sub to construct
                            return subArgs.Any()
                                ? FnSub(subPattern, subArgs)
                                : Regex.IsMatch(subPattern, SUBVARIABLE_PATTERN)
                                ? FnSub(subPattern)
                                : subPattern;
                        }
                    }
                    return map;
                case IList<object> list:
                    return list.Select(ResolveResourceNamesToLogicalIds).ToList();
                case null:
                    AddError("null value is not allowed");
                    return value;
                case string _:

                    // nothing further to substitute
                    return value;
                default:

                    // nothing further to substitute
                    DebugWriteLine($"SKIPPING: {value.GetType()}");
                    return value;
                }
            }
        }
    }
}