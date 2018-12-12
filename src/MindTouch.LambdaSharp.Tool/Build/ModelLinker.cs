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

namespace MindTouch.LambdaSharp.Tool.Build {
    using static ModelFunctions;

    public class ModelLinker : AModelProcessor {

        //--- Class Methods ---
        private static void DebugWriteLine(Func<string> lazyMessage) {
#if false
            Console.WriteLine(lazyMessage());
#endif
        }

        //--- Fields ---
        private Dictionary<string, AModuleEntry> _freeEntries = new Dictionary<string, AModuleEntry>();
        private Dictionary<string, AModuleEntry> _boundEntries = new Dictionary<string, AModuleEntry>();

        //--- Constructors ---
        public ModelLinker(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Process(ModuleBuilder builder) {
            _freeEntries.Clear();
            _boundEntries.Clear();

            // compute scopes
            AtLocation("Entries", () => {
                var functionNames = builder.Entries.OfType<FunctionEntry>()
                    .Select(function => function.FullName)
                    .ToList();
                foreach(var entry in builder.Entries) {
                    AtLocation(entry.FullName, () => {
                        if(entry.Scope.Contains("*")) {
                            entry.Scope = entry.Scope
                                .Where(scope => scope != "*")
                                .Union(functionNames)
                                .Distinct()
                                .OrderBy(item => item)
                                .ToList();
                        }

                        // verify that all defined scope values are valid
                        foreach(var unknownScope in entry.Scope.Where(scope => !functionNames.Contains(scope))) {
                            AddError($"unknown referenced function '{unknownScope}' in scope definition");
                        }
                    });
                }
            });

            // compute function environments
            AtLocation("Functions", () => {
                foreach(var function in builder.Entries.OfType<FunctionEntry>()) {
                    AtLocation(function.FullName, () => {
                        var environment = function.Function.Environment.Variables;

                        // set default environment variables
                        environment["MODULE_NAME"] = builder.Name;
                        environment["MODULE_ID"] = FnRef("AWS::StackName");
                        environment["MODULE_VERSION"] = builder.Version.ToString();
                        environment["LAMBDA_NAME"] = function.FullName;
                        environment["LAMBDA_RUNTIME"] = function.Function.Runtime;
                        if(builder.HasLambdaSharpDependencies) {
                            environment["DEADLETTERQUEUE"] = FnRef("Module::DeadLetterQueueArn");
                            environment["DEFAULTSECRETKEY"] = FnRef("Module::DefaultSecretKeyArn");
                        }

                        // add all entries scoped to this function
                        foreach(var scopeEntry in builder.Entries.Where(e => e.Scope.Contains(function.FullName))) {
                            var prefix = scopeEntry.HasSecretType ? "SEC_" : "STR_";
                            var fullEnvName = prefix + scopeEntry.FullName.Replace("::", "_").ToUpperInvariant();
                            environment[fullEnvName] = (dynamic)scopeEntry.GetExportReference();
                        }

                        // add all explicitly listed environment variables
                        foreach(var kv in function.Environment) {

                            // add explicit environment variable as string value
                            var fullEnvName = "STR_" + kv.Key.Replace("::", "_").ToUpperInvariant();
                            environment[fullEnvName] = (dynamic)kv.Value;
                        }
                    });
                }
            });

            // compute exports
            AtLocation("Outputs", () => {
                foreach(var output in builder.Outputs.OfType<ExportOutput>()) {
                    AtLocation(output.Name, () => {
                        if(output.Value == null) {

                            // NOTE (2018-12-11, bjorg): if no value is provided, we expect the export name to correspond to an
                            //  entry name; if it does, we export the ARN value of that parameter; in addition, we copy its
                            // description if none is provided.

                            if(!builder.TryGetEntry(output.Name, out AModuleEntry entry)) {
                                AddError("could not find matching entry");
                                output.Value = "<BAD>";
                            } else {
                                output.Value = entry.GetExportReference();
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
            AtLocation("Entries", () => {
                DiscoverEntries();
                ResolveEntries();
                ReportUnresolvedEntries();
            });
            if(Settings.HasErrors) {
                return;
            }

            // resolve all references
            builder.VisitAll(item => Substitute(item, ReportMissingReference));
            builder.VisitAll(Finalize);

            // local functions
            void DiscoverEntries() {
                foreach(var entry in builder.Entries) {
                    AtLocation(entry.FullName, () => {
                        switch(entry.Reference) {
                        case null:
                            throw new ApplicationException($"entry reference cannot be null: {entry.FullName}");
                        case string _:
                            _freeEntries[entry.FullName] = entry;
                            DebugWriteLine(() => $"FREE => {entry.FullName}");
                            break;
                        case IList<object> list:
                            if(list.All(value => value is string)) {
                                _freeEntries[entry.FullName] = entry;
                                DebugWriteLine(() => $"FREE => {entry.FullName}");
                            } else {
                                _boundEntries[entry.FullName] = entry;
                                DebugWriteLine(() => $"BOUND => {entry.FullName}");
                            }
                            break;
                        default:
                            _boundEntries[entry.FullName] = entry;
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
                    foreach(var entry in _boundEntries.Values.ToList()) {
                        AtLocation(entry.FullName, () => {

                            // NOTE (2018-10-04, bjorg): each iteration, we loop over a bound entry;
                            //  in the iteration, we attempt to substitute all references with free entries;
                            //  if we do, the entry can be added to the pool of free entries;
                            //  if we iterate over all bound entries without making progress, then we must have
                            //  a circular dependency and we stop.

                            var doesNotContainBoundEntries = true;
                            AtLocation("Reference", () => {
                                entry.Reference = Substitute(entry.Reference, (string missingName) => {
                                    doesNotContainBoundEntries = doesNotContainBoundEntries && !_boundEntries.ContainsKey(missingName);
                                });
                            });
                            if(doesNotContainBoundEntries) {

                                // capture that progress towards resolving all bound entries has been made;
                                // if ever an iteration does not produces progress, we need to stop; otherwise
                                // we will loop forever
                                progress = true;

                                // promote bound entry to free entry
                                _freeEntries[entry.FullName] = entry;
                                _boundEntries.Remove(entry.FullName);
                                DebugWriteLine(() => $"RESOLVED => {entry.FullName} = {Newtonsoft.Json.JsonConvert.SerializeObject(entry.Reference)}");
                            }
                        });
                    }
                } while(progress);
            }

            void ReportUnresolvedEntries() {
                foreach(var entry in builder.Entries) {
                    AtLocation(entry.FullName, () => {
                        Substitute(entry.Reference, ReportMissingReference);
                    });
                }
            }

            void ReportMissingReference(string missingName) {
                if(_boundEntries.ContainsKey(missingName)) {
                    AddError($"circular !Ref dependency on '{missingName}'");
                } else {
                    AddError($"could not find !Ref dependency '{missingName}'");
                }
            }
        }

        private object Substitute(object root, Action<string> missing = null) {
            return Visit(root, value => {

                // handle !Ref expression
                if(TryGetFnRef(value, out string refKey)) {
                    if(TrySubstitute(refKey, null, out object found)) {
                        return found ?? value;
                    }
                    DebugWriteLine(() => $"NOT FOUND => {refKey}");
                    missing?.Invoke(refKey);
                    return value;
                }

                // handle !GetAtt expression
                if(TryGetFnGetAtt(value, out string getAttKey, out string getAttAttribute)) {
                    if(TrySubstitute(getAttKey, getAttAttribute, out object found)) {
                        return found ?? value;
                    }
                    DebugWriteLine(() => $"NOT FOUND => {getAttKey}");
                    missing?.Invoke(getAttKey);
                    return value;
                }

                // handle !Sub expression
                if(TryGetFnSub(value, out string subPattern, out IDictionary<string, object> subArgs)) {

                    // replace as many ${VAR} occurrences as possible
                    var substitions = false;
                    subPattern = ReplaceSubPattern(subPattern, (subRefKey, suffix) => {
                        if(!subArgs.ContainsKey(subRefKey)) {
                            if(TrySubstitute(subRefKey, suffix?.Substring(1), out object found)) {
                                if(found == null) {
                                    return null;
                                }
                                substitions = true;
                                if(found is string text) {
                                    return text;
                                }

                                // substitute found value as new argument
                                var argName = $"P{subArgs.Count}";
                                subArgs.Add(argName, found);
                                return "${" + argName + "}";
                            }
                            DebugWriteLine(() => $"NOT FOUND => {subRefKey}");
                            missing?.Invoke(subRefKey);
                        }
                        return null;
                    });
                    if(!substitions) {
                        return value;
                    }
                    return FnSub(subPattern, subArgs);
                }
                return value;
            });

            // local functions
            bool TrySubstitute(string key, string attribute, out object found) {
                found = null;
                if(key.StartsWith("AWS::", StringComparison.Ordinal)) {

                    // built-in AWS references can be kept as-is
                    return true;
                } else if(key.StartsWith("@", StringComparison.Ordinal)) {

                    // module resource names must be kept as-is
                    return true;
                }

                // check if the requested key can be resolved using a free entry
                if(_freeEntries.TryGetValue(key, out AModuleEntry freeEntry)) {
                    if(attribute != null) {
                        switch(freeEntry) {
                        case PackageEntry _:
                        case InputEntry _:
                        case VariableEntry _:
                            AddError($"reference '{key}' must be a reference, resource, or function when using Fn::GetAtt");
                            break;
                        case FunctionEntry _:
                        case ResourceEntry _:
                            if(!freeEntry.HasPragma("skip-type-validation") && !ResourceMapping.HasAttribute(freeEntry.Type, attribute)) {
                                AddError($"resource type {freeEntry.Type} does not have attribute '{attribute}'");
                            }

                            // attributes can be used with managed resources/functions
                            found = FnGetAtt(freeEntry.ResourceName, attribute);
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

        private object Finalize(object root) {
            return Visit(root, value => {

                // handle !Ref expression
                if(TryGetFnRef(value, out string refKey) && refKey.StartsWith("@", StringComparison.Ordinal)) {
                    return FnRef(refKey.Substring(1));
                }

                // handle !GetAtt expression
                if(TryGetFnGetAtt(value, out string getAttKey, out string getAttAttribute) && getAttKey.StartsWith("@", StringComparison.Ordinal)) {
                    return FnGetAtt(getAttKey.Substring(1), getAttAttribute);
                }

                // handle !Sub expression
                if(TryGetFnSub(value, out string subPattern, out IDictionary<string, object> subArgs)) {

                    // replace as many ${VAR} occurrences as possible
                    subPattern = ReplaceSubPattern(subPattern, (subRefKey, suffix) => {
                        if(!subArgs.ContainsKey(subRefKey) && subRefKey.StartsWith("@", StringComparison.Ordinal)) {
                            return "${" + subRefKey.Substring(1) + suffix + "}";
                        }
                        return null;
                    });
                    return FnSub(subPattern, subArgs);
                }
                return value;
            });
        }

        private object Visit(object value, Func<object, object> visitor) {
            switch(value) {
            case IDictionary dictionary: {
                    var map = new Dictionary<string, object>();
                    foreach(DictionaryEntry entry in dictionary) {
                        AtLocation((string)entry.Key, () => {
                            map.Add((string)entry.Key, Visit(entry.Value, visitor));
                        });
                    }
                    var visitedMap = visitor(map);

                    // check if visitor replaced the instance
                    if(!object.ReferenceEquals(visitedMap, map)) {
                        return visitedMap;
                    }

                    // update existing instance in-place
                    foreach(var entry in map) {
                        dictionary[entry.Key] = entry.Value;
                    }
                    return value;
                }
            case IList list: {
                    for(var i = 0; i < list.Count; ++i) {
                        AtLocation($"[{i + 1}]", () => {
                            list[i] = Visit(list[i], visitor);
                        });
                    }
                    return visitor(value);
                }
            case null:
                AddError("null value is not allowed");
                return value;
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
                                if((propertyValue != null) && !SkipType(propertyValue.GetType())) {
                                    property.GetSetMethod()?.Invoke(value, new[] {
                                        Visit(propertyValue, visitor)
                                    });
                                }
                            } catch(Exception e) {
                                throw new ApplicationException($"unable to get/set {value.GetType()}::{property.Name}", e);
                            }
                        });
                    }
                    return visitor(value);
                }
                throw new ApplicationException($"unsupported type: {value.GetType()}");
            }

            // local function
            bool SkipType(Type type) => type.IsValueType || type == typeof(string);
        }
    }
}