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
using System.Text;
using System.Text.RegularExpressions;
using Humidifier;
using MindTouch.LambdaSharp.Tool.Model;
using Newtonsoft.Json;

namespace MindTouch.LambdaSharp.Tool.Cli.Build {
    using static ModelFunctions;

    public class ModelLinker : AModelProcessor {

        //--- Class Methods ---
        private static void DebugWriteLine(Func<string> lazyMessage) {
#if false
            var text = lazyMessage();
            if(text != null) {
                Console.WriteLine(text);
            }
#endif
        }


        //--- Fields ---
        private ModuleBuilder _builder;
        private Dictionary<string, AModuleEntry> _freeEntries = new Dictionary<string, AModuleEntry>();
        private Dictionary<string, AModuleEntry> _boundEntries = new Dictionary<string, AModuleEntry>();

        //--- Constructors ---
        public ModelLinker(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods ---
        public void Process(ModuleBuilder builder) {
            _builder = builder;
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
            AtLocation("Declarations", () => {
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
                            if(function.HasDeadLetterQueue) {
                                environment["DEADLETTERQUEUE"] = FnRef("Module::DeadLetterQueueArn");
                            }
                            environment["DEFAULTSECRETKEY"] = FnRef("Module::DefaultSecretKeyArn");
                        }

                        // add all entries scoped to this function
                        foreach(var scopeEntry in builder.Entries.Where(e => e.Scope.Contains(function.FullName))) {
                            var prefix = scopeEntry.HasSecretType ? "SEC_" : "STR_";
                            var fullEnvName = prefix + scopeEntry.FullName.Replace("::", "_").ToUpperInvariant();

                            // check if entry has a condition associated with it
                            environment[fullEnvName] = (dynamic)(
                                ((scopeEntry is AResourceEntry resourceEntry) && (resourceEntry.Condition != null))
                                ? FnIf(resourceEntry.Condition, scopeEntry.GetExportReference(), FnRef("AWS::NoValue"))
                                : scopeEntry.GetExportReference()
                            );
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
            builder.VisitAll((entry, item) => Substitute(entry, item, ReportMissingReference));

            // remove any optional entries that are unreachable
            DiscardUnreachableEntries();

            // replace all references with their logical IDs
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
                                entry.Reference = Substitute(entry, entry.Reference, (string missingName) => {
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
                        Substitute(entry, entry.Reference, ReportMissingReference);
                    });
                }
            }

            void ReportMissingReference(string missingName) {
                if(_boundEntries.ContainsKey(missingName)) {
                    AddError($"circular !Ref dependency on '{missingName}'");
                } else {
                    AddError($"could not find '{missingName}'");
                }
            }
        }

        private object Substitute(AModuleEntry entry, object root, Action<string> missing = null) {
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

                // handle !If expression
                if(TryGetFnIf(value, out string condition, out object ifTrue, out object ifFalse)) {
                    if(condition.StartsWith("@", StringComparison.Ordinal)) {
                        return value;
                    }
                    if(_freeEntries.TryGetValue(condition, out AModuleEntry freeEntry)) {
                        if(!(freeEntry is ConditionEntry)) {
                            AddError($"entry '{freeEntry.FullName}' must be a condition");
                        }
                        return FnIf(freeEntry.ResourceName, ifTrue, ifFalse);
                    }
                    DebugWriteLine(() => $"NOT FOUND => {condition}");
                    missing?.Invoke(condition);
                }

                // handle !Condition expression
                if(TryGetFnCondition(value, out condition)) {
                    if(condition.StartsWith("@", StringComparison.Ordinal)) {
                        return value;
                    }
                    if(_freeEntries.TryGetValue(condition, out AModuleEntry freeEntry)) {
                        if(!(freeEntry is ConditionEntry)) {
                            AddError($"entry '{freeEntry.FullName}' must be a condition");
                        }
                        return FnCondition(freeEntry.ResourceName);
                    }
                    DebugWriteLine(() => $"NOT FOUND => {condition}");
                    missing?.Invoke(condition);
                }

                // handle !FindInMap expression
                if(TryGetFnFindInMap(value, out string mapName, out object topLevelKey, out object secondLevelKey)) {
                    if(mapName.StartsWith("@", StringComparison.Ordinal)) {
                        return value;
                    }
                    if(_freeEntries.TryGetValue(mapName, out AModuleEntry freeEntry)) {
                        if(!(freeEntry is MappingEntry)) {
                            AddError($"entry '{freeEntry.FullName}' must be a mapping");
                        }
                        return FnFindInMap(freeEntry.ResourceName, topLevelKey, secondLevelKey);
                    }
                    DebugWriteLine(() => $"NOT FOUND => {mapName}");
                    missing?.Invoke(mapName);
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
                    if(freeEntry is ConditionEntry) {
                        AddError($"condition '{freeEntry.FullName}' cannot be used here");
                    } else if(attribute != null) {
                        if(freeEntry.HasTypeValidation && !_builder.HasAttribute(freeEntry, attribute)) {
                            AddError($"entry '{freeEntry.FullName}' of type '{freeEntry.Type}' does not have attribute '{attribute}'");
                        }

                        // attributes can be used with managed resources/functions
                        found = FnGetAtt(freeEntry.ResourceName, attribute);
                    } else {
                        found = freeEntry.Reference;
                    }
                    return true;
                }
                return false;
            }
        }

        private void DiscardUnreachableEntries() {
            var reachable = new Dictionary<string, AModuleEntry>();
            var found = new Dictionary<string, AModuleEntry>();
            var unused = new Dictionary<string, AModuleEntry>();
            var foundEntriesToRemove = true;
            while(foundEntriesToRemove) {
                foundEntriesToRemove = false;
                reachable.Clear();
                found.Clear();
                foreach(var entry in _builder.Entries.OfType<AResourceEntry>().Where(res => !res.DiscardIfNotReachable)) {
                    found[entry.FullName] = entry;
                    entry.Visit(FindReachable);
                }
                foreach(var output in _builder.Entries.OfType<AOutputEntry>()) {
                    output.Visit(FindReachable);
                }
                foreach(var statement in _builder.ResourceStatements) {
                    FindReachable(null, statement);
                }
                while(found.Any()) {

                    // record found names as reachable
                    foreach(var kv in found) {
                        reachable[kv.Key] = kv.Value;
                    }

                    // detect what is reachable from found entries entry
                    var current = found;
                    found = new Dictionary<string, AModuleEntry>();
                    foreach(var kv in current) {
                        kv.Value.Visit(FindReachable);
                    }
                }
                foreach(var entry in _builder.Entries.ToList()) {
                    if(!reachable.ContainsKey(entry.FullName)) {
                        if(entry.DiscardIfNotReachable) {
                            foundEntriesToRemove = true;
                            DebugWriteLine(() => $"DISCARD '{entry.FullName}'");
                            _builder.RemoveEntry(entry.FullName);
                        } else if(entry is InputEntry) {
                            switch(entry.FullName) {
                            case "Secrets":
                            case "DeploymentBucketName":
                            case "DeploymentPrefix":
                            case "DeploymentPrefixLowercase":
                            case "DeploymentParent":
                            case "DeploymentChecksum":

                                // these are built-in parameters; don't report them
                                break;
                            default:
                                unused[entry.FullName] = entry;
                                break;
                            }
                        }
                    }
                }
            }
            foreach(var entry in unused.Values.OrderBy(e => e.FullName)) {
                AddWarning($"'{entry.FullName}' is defined but never used");

            }

            // local functions
            object FindReachable(AModuleEntry entry, object root) {
                return Visit(root, value => {

                    // handle !Ref expression
                    if(TryGetFnRef(value, out string refKey)) {
                        MarkReachableEntry(entry, refKey);
                        return value;
                    }

                    // handle !GetAtt expression
                    if(TryGetFnGetAtt(value, out string getAttKey, out string getAttAttribute)) {
                        MarkReachableEntry(entry, getAttKey);
                        return value;
                    }

                    // handle !Sub expression
                    if(TryGetFnSub(value, out string subPattern, out IDictionary<string, object> subArgs)) {

                        // replace as many ${VAR} occurrences as possible
                        subPattern = ReplaceSubPattern(subPattern, (subRefKey, suffix) => {
                            if(!subArgs.ContainsKey(subRefKey)) {
                                MarkReachableEntry(entry, subRefKey);
                                return "${" + subRefKey.Substring(1) + suffix + "}";
                            }
                            return null;
                        });
                        return value;
                    }

                    // handle !If expression
                    if(TryGetFnIf(value, out string condition, out object _, out object _)) {
                        MarkReachableEntry(entry, condition);
                        return value;
                    }

                    // handle !Condition expression
                    if(TryGetFnCondition(value, out condition)) {
                        MarkReachableEntry(entry, condition);
                        return value;
                    }

                    // handle !FindInMap expression
                    if(TryGetFnFindInMap(value, out string mapName, out object _, out object _)) {
                        MarkReachableEntry(entry, mapName);
                        return value;
                    }
                    return value;
                });
            }

            void MarkReachableEntry(AModuleEntry entry, string fullNameOrResourceName) {
                if(fullNameOrResourceName.StartsWith("AWS::", StringComparison.Ordinal)) {
                    return;
                }
                if(_builder.TryGetEntry(fullNameOrResourceName, out AModuleEntry refEntry)) {
                    if(!reachable.ContainsKey(refEntry.FullName)) {
                        if(!found.ContainsKey(refEntry.FullName)) {
                            DebugWriteLine(() => $"REACHED {entry?.FullName ?? "<null>"} -> {refEntry?.FullName ?? "<null>"}");
                        }
                        found[refEntry.FullName] = refEntry;
                    }
                }
            }
        }

        private object Finalize(AModuleEntry entry, object root) {
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

                // handle !If expression
                if(TryGetFnIf(value, out string condition, out object ifTrue, out object ifFalse) && condition.StartsWith("@", StringComparison.Ordinal)) {
                    return FnIf(condition.Substring(1), ifTrue, ifFalse);
                }

                // handle !Condition expression
                if(TryGetFnCondition(value, out condition) && condition.StartsWith("@", StringComparison.Ordinal)) {
                    return FnCondition(condition.Substring(1));
                }

                // handle !Condition expression
                if(TryGetFnFindInMap(value, out string mapName, out object topLevelKey, out object secondLevelKey) && mapName.StartsWith("@", StringComparison.Ordinal)) {
                    return FnFindInMap(mapName.Substring(1), topLevelKey, secondLevelKey);
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
                        AtLocation($"{i + 1}", () => {
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
                            object propertyValue;
                            try {
                                propertyValue = property.GetGetMethod()?.Invoke(value, new object[0]);
                            } catch(Exception e) {
                                throw new ApplicationException($"unable to get {value.GetType()}::{property.Name}", e);
                            }
                            if((propertyValue == null) || SkipType(propertyValue.GetType())) {
                                return;
                            }
                            propertyValue = Visit(propertyValue, visitor);
                            try {
                                property.GetSetMethod()?.Invoke(value, new[] { propertyValue });
                            } catch(Exception e) {
                                throw new ApplicationException($"unable to set {value.GetType()}::{property.Name}", e);
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