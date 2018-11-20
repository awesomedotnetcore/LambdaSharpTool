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
using System.Text.RegularExpressions;
using Humidifier;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool {

    public class ModelReferenceResolver : AModelProcessor {

        //--- Constants ---
        private const string SUBVARIABLE_PATTERN = @"\$\{(?!\!)[^\}]+\}";

        //--- Class Methods ---
        private static void DebugWriteLine(Func<string> lazyMessage) {
#if false
            Console.WriteLine(lazyMessage());
#endif
        }

        //--- Constructors ---
        public ModelReferenceResolver(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Resolve(Module module) {

            // resolve scopes
            var functionNames = module.GetAllEntriesOfType<FunctionParameter>()
                .Select(function => function.FullName)
                .ToList();
            foreach(var entry in module.Entries) {
                if(entry.Scope.Contains("*")) {
                    entry.Scope = entry.Scope
                        .Where(scope => scope != "*")
                        .Union(functionNames)
                        .Distinct()
                        .OrderBy(item => item)
                        .ToList();
                }
            }

            // resolve exports
            AtLocation("Outputs", () => {
                foreach(var output in module.Outputs.OfType<ExportOutput>()) {
                    AtLocation(output.Name, () => {
                        if(output.Value == null) {

                            // NOTE: if no value is provided, we expect the export name to correspond to a
                            //  parameter name; if it does, we export the ARN value of that parameter; in
                            //  addition, we assume its description if none is provided.

                            if(!module.TryGetEntry(output.Name, out ModuleEntry entry)) {
                                AddError("could not find matching entry");
                                output.Value = "<BAD>";
                            } else {
                                output.Value = entry.Reference;
                            }

                            // only set the description if the value was not set
                            if(output.Description == null) {
                                output.Description = entry.Description;
                            }
                        }
                    });
                }
            });

            // resolve all inter-entry references
            var freeEntries = new Dictionary<string, ModuleEntry>();
            var boundEntries = new Dictionary<string, ModuleEntry>();
            AtLocation("Entries", () => {
                DiscoverEntries();
                ResolveEntries();
                ReportUnresolvedEntries();
            });
            if(Settings.HasErrors) {
                return;
            }

            // resolve everything to logical ids
            AtLocation("Secrets", () => {
                module.Secrets = (IList<object>)Substitute(module.Secrets);
            });
            AtLocation("Conditions", () => {
                module.Conditions = (IDictionary<string, object>)Substitute(module.Conditions);
            });
            AtLocation("Entries", () => {
                foreach(var entry in module.Entries) {
                    AtLocation(entry.FullName, () => {
                        switch(entry.Resource) {
                        case InputParameter _:
                        case ValueParameter _:
                        case PackageParameter _:

                            // nothing to do
                            break;
                        case HumidifierParameter humidifierParameter:
                            AtLocation("Resources", () => {
                                humidifierParameter.Resource = (Humidifier.Resource)Substitute(humidifierParameter.Resource, ReportMissingReference);
                            });
                            AtLocation("DependsOn", () => {
                                humidifierParameter.DependsOn = humidifierParameter.DependsOn.Select(dependency => module.GetEntry(dependency).LogicalId).ToList();
                            });
                            break;
                        case FunctionParameter functionParameter:
                            AtLocation("Environment", () => {
                                functionParameter.Environment = (IDictionary<string, object>)Substitute(functionParameter.Environment, ReportMissingReference);
                            });
                            AtLocation("Function", () => {
                                functionParameter.Function = (Humidifier.Lambda.Function)Substitute(functionParameter.Function, ReportMissingReference);
                            });

                            // update function sources
                            AtLocation("Sources", () => {
                                var index = 0;
                                foreach(var source in functionParameter.Sources) {
                                    AtLocation($"[{++index}]", () => {
                                        switch(source) {
                                        case AlexaSource alexaSource:
                                            if(alexaSource.EventSourceToken != null) {
                                                alexaSource.EventSourceToken = Substitute(alexaSource.EventSourceToken, ReportMissingReference);
                                            }
                                            break;
                                        }
                                    });
                                }
                            });
                            break;
                        default:
                            throw new ApplicationException($"unexpected type: {entry.Resource.GetType()}");
                        }
                    });
                }
            });

            // resolve references in output values
            foreach(var output in module.Outputs) {
                switch(output) {
                case ExportOutput exportOutput:
                    exportOutput.Value = Substitute(exportOutput.Value, ReportMissingReference);
                    break;
                case CustomResourceHandlerOutput _:
                case MacroOutput _:

                    // nothing to do
                    break;
                default:
                    throw new InvalidOperationException($"cannot resolve references for this type: {output?.GetType()}");
                }
            }

            // resolve references in grants
            AtLocation("Grants", () => {
                foreach(var grant in module.Grants) {
                    AtLocation(grant.Sid, () => {
                        grant.References = Substitute(grant.References, ReportMissingReference);
                    });
                }
            });

            // local functions
            void DiscoverEntries() {
                foreach(var entry in module.Entries) {
                    AtLocation(entry.FullName, () => {
                        switch(entry.Reference) {
                        case null:
                            throw new ApplicationException($"entry reference cannot be null: {entry.FullName}");
                        case string _:
                            freeEntries[entry.FullName] = entry;
                            DebugWriteLine(() => $"FREE => {entry.FullName}");
                            break;
                        case IList<object> list:
                            if(list.All(value => value is string)) {
                                freeEntries[entry.FullName] = entry;
                                DebugWriteLine(() => $"FREE => {entry.FullName}");
                            } else {
                                boundEntries[entry.FullName] = entry;
                                DebugWriteLine(() => $"BOUND => {entry.FullName}");
                            }
                            break;
                        default:
                            boundEntries[entry.FullName] = entry;
                            DebugWriteLine(() => $"BOUND => {entry.FullName}");
                            break;
                        }
                    });
                }
            }

            void ResolveEntries() {
                bool progress;
                do {
                    progress = false;
                    foreach(var entry in boundEntries.Values.ToList()) {
                        AtLocation(entry.FullName, () => {

                            // NOTE (2018-10-04, bjorg): each iteration, we loop over a bound entry;
                            //  in the iteration, we attempt to substitute all references with free entries;
                            //  if we do, the entry can be added to the pool of free entries;
                            //  if we iterate over all bound entries without making progress, then we must have
                            //  a circular dependency and we stop.

                            var doesNotContainBoundEntries = true;
                            AtLocation("Reference", () => {
                                entry.Reference = Substitute(entry.Reference, (string missingName) => {
                                    doesNotContainBoundEntries = doesNotContainBoundEntries && !boundEntries.ContainsKey(missingName);
                                });
                            });
                            if(doesNotContainBoundEntries) {

                                // capture that progress towards resolving all bound entries has been made;
                                // if ever an iteration does not produces progress, we need to stop; otherwise
                                // we will loop forever
                                progress = true;

                                // promote bound entry to free entry
                                freeEntries[entry.FullName] = entry;
                                boundEntries.Remove(entry.FullName);
                                DebugWriteLine(() => $"RESOLVED => {entry.FullName} = {Newtonsoft.Json.JsonConvert.SerializeObject(entry.Reference)}");
                            }
                        });
                    }
                } while(progress);
            }

            void ReportUnresolvedEntries() {
                foreach(var entry in module.Entries) {
                    AtLocation(entry.FullName, () => {
                        Substitute(entry.Reference, ReportMissingReference);
                    });
                }
            }

            void ReportMissingReference(string missingName) {
                if(boundEntries.ContainsKey(missingName)) {
                    AddError($"circular !Ref dependency on '{missingName}'");
                } else {
                    AddError($"could not find !Ref dependency '{missingName}'");
                }
            }

            object Substitute(object value, Action<string> missing = null) {
                switch(value) {
                case IDictionary dictionary: {
                        var map = new Dictionary<object, object>();
                        foreach(DictionaryEntry entry in dictionary) {
                            AtLocation((string)entry.Key, () => {
                                map.Add(entry.Key, Substitute(entry.Value, missing));
                            });
                        }
                        foreach(var entry in map) {
                            dictionary[entry.Key] = entry.Value;
                        }
                        if(map.Count == 1) {

                            // handle !Ref expression
                            if(map.TryGetValue("Ref", out object refObject) && (refObject is string refKey)) {
                                if(TrySubstitute(refKey, null, out object found)) {
                                    return found ?? value;
                                }
                                DebugWriteLine(() => $"NOT FOUND => {refKey}");
                                missing?.Invoke(refKey);
                                return value;
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
                                DebugWriteLine(() => $"NOT FOUND => {getAttKey}");
                                missing?.Invoke(getAttKey);
                                return value;
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
                                    return value;
                                }

                                // replace as many ${VAR} occurrences as possible
                                var substitions = false;
                                subPattern = Regex.Replace(subPattern, SUBVARIABLE_PATTERN, match => {
                                    var matchText = match.ToString();
                                    var name = matchText.Substring(2, matchText.Length - 3).Trim().Split('.', 2);
                                    var suffix = (name.Length == 2) ? ("." + name[1]) : null;
                                    var subRefKey = name[0];
                                    if(!subArgs.ContainsKey(subRefKey)) {
                                        if(TrySubstitute(subRefKey, suffix?.Substring(1), out object found)) {
                                            if(found == null) {
                                                return matchText;
                                            }
                                            substitions = true;
                                            if(found is string text) {
                                                return text;
                                            }
                                            if((found is IDictionary<string, object> subMap) && (subMap.Count == 1)) {

                                                // check if found value is a !Ref expression that can be inlined
                                                if(subMap.TryGetValue("Ref", out object subMapRefObject)) {
                                                    if(name.Length == 2) {
                                                        return "${" + subMapRefObject + suffix + "}";
                                                    }
                                                    return "${" + subMapRefObject + "}";
                                                }

                                                // check if found value is a !GetAtt expression that can be inlined
                                                if(
                                                    subMap.TryGetValue("Fn::GetAtt", out object subMapGetAttObject)
                                                    && (subMapGetAttObject is IList<object> subMapGetAttArgs)
                                                    && (subMapGetAttArgs.Count == 2)
                                                ) {
                                                    return "${" + subMapGetAttArgs[0] + "." + subMapGetAttArgs[1] + "}";
                                                }
                                            }

                                            // substitute found value as new argument
                                            var argName = $"P{subArgs.Count}";
                                            subArgs.Add(argName, found);
                                            return "${" + argName + "}";
                                        }
                                        DebugWriteLine(() => $"NOT FOUND => {subRefKey}");
                                        missing?.Invoke(subRefKey);
                                    }
                                    return matchText;
                                });
                                if(!substitions) {
                                    return value;
                                }

                                // determine which form of !Sub to construct
                                return subArgs.Any()
                                    ? FnSub(subPattern, subArgs)
                                    : Regex.IsMatch(subPattern, SUBVARIABLE_PATTERN)
                                    ? FnSub(subPattern)
                                    : subPattern;
                            }
                        }
                        return value;
                    }
                case IList list: {
                        for(var i = 0; i < list.Count; ++i) {
                            AtLocation($"[{i + 1}]", () => {
                                list[i] = Substitute(list[i], missing);
                            });
                        }
                        return value;
                    }
                case null:
                    throw new ApplicationException("null value is not allowed");
                default:
                    if(SkipType(value.GetType())) {

                        // nothing further to substitute
                        return value;
                    }
                    if(value.GetType().FullName.StartsWith("Humidifier.", StringComparison.Ordinal)) {

                        // use reflection to substitute properties
                        foreach(var property in value.GetType().GetProperties().Where(p => !SkipType(p.PropertyType))) {
                            AtLocation(property.Name, () => {
                                try {
                                    var propertyValue = property.GetGetMethod()?.Invoke(value, new object[0]);
                                    if((propertyValue != null) && !propertyValue.GetType().IsValueType) {
                                        property.GetSetMethod()?.Invoke(value, new[] { Substitute(propertyValue, missing) });
                                        DebugWriteLine(() => $"UPDATED => {value.GetType()}::{property.Name} [{property.PropertyType}]");
                                    }
                                } catch(Exception e) {
                                    throw new ApplicationException($"unable to get/set {value.GetType()}::{property.Name}", e);
                                }
                            });
                        }
                        return value;
                    }
                    throw new ApplicationException($"unsupported type: {value.GetType()}");
                }

                // local function
                bool SkipType(Type type) => type.IsValueType || type == typeof(string);
            }

            bool TrySubstitute(string key, string attribute, out object found) {
                found = null;
                if(key.StartsWith("AWS::", StringComparison.Ordinal)) {

                    // built-in AWS references can be kept as-is
                    return true;
                } else if(key.StartsWith("@", StringComparison.Ordinal)) {

                    // TODO: remove @ prefix from resource names
                    // if(final) {
                    //     found = (attribute != null)
                    //         ? FnGetAtt(key.Substring(1), attribute)
                    //         : FnRef(key.Substring(1));
                    // }

                    // module resource names can be kept as-is
                    return true;
                }

                // check if the requested key can be resolved using a free entry
                var visited = new HashSet<string>();
            again:
                if(freeEntries.TryGetValue(key, out ModuleEntry freeEntry)) {
                    if(attribute != null) {
                        switch(freeEntry.Resource) {
                        case PackageParameter _:
                        case InputParameter _:
                            AddError($"reference '{key}' must be a reference, resource, or function when using Fn::GetAtt");
                            break;
                        case ValueParameter _:
                            if(
                                (freeEntry.Reference is IDictionary<string, object> map)
                                && (map.Count == 1)
                                && map.TryGetValue("Ref", out object refObject)
                                && (refObject is string refValue)
                            ) {

                                // check the key hasn't been visited previously to avoid infinite loops
                                if(visited.Add(key)) {
                                    key = refValue;
                                    goto again;
                                }
                                AddError($"reference '{key}' has a circular dependency on '{refValue}'");
                            } else {
                                AddError($"reference '{key}' must be a reference, resource, or function when using Fn::GetAtt");
                            }
                            break;
                        case FunctionParameter _:
                        case HumidifierParameter _:
                            found = FnGetAtt(key, attribute);
                            break;
                        }
                    } else {
                        found = freeEntry.Reference;
                    }
                    return true;
                }
                return false;
            }
        }
    }
}