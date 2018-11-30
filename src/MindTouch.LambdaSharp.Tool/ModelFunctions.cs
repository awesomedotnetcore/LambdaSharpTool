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

namespace MindTouch.LambdaSharp.Tool {

    public static class ModelFunctions {

        //--- Constants ---
        private const string SUBVARIABLE_PATTERN = @"\$\{(?!\!)[^\}]+\}";

        //--- Class Methods ---
        public static object FnGetAtt(string logicalNameOfResource, string attributeName)
            => new Dictionary<string, object> {
                ["Fn::GetAtt"] = new List<object> {
                    logicalNameOfResource,
                    attributeName
                }
            };

        public static object FnIf(string conditionName, object valueIfTrue, object valueIfFalse)
            => new Dictionary<string, object> {
                ["Fn::If"] = new List<object> {
                    conditionName,
                    valueIfTrue,
                    valueIfFalse
                }
            };

        public static object FnImportValue(object sharedValueToImport)
            => new Dictionary<string, object> {
                ["Fn::ImportValue"] = sharedValueToImport
            };

        public static object FnJoin(string separator, IEnumerable<object> parameters) {

            // attempt to concatenate as many values as possible
            var processed = new List<object>();
            foreach(var parameter in parameters) {
                if(processed.Any() && (parameter is string currentText)) {
                    if(processed.Last() is string lastText) {
                        processed[processed.Count - 1] = lastText + separator + currentText;
                    } else {
                        processed.Add(parameter);
                    }
                } else {
                    processed.Add(parameter);
                }
            }
            var count = processed.Count();
            if(count == 0) {
                return "";
            }
            if(count == 1) {
                return processed.First();
            }
            return new Dictionary<string, object> {
                ["Fn::Join"] = new List<object> {
                    separator,
                    processed
                }
            };
        }

        public static object FnJoin(string separator, object parameters) {
            return new Dictionary<string, object> {
                ["Fn::Join"] = new List<object> {
                    separator,
                    parameters
                }
            };
        }

        public static object FnRef(string reference)
            => new Dictionary<string, object> {
                ["Ref"] = reference
            };

        public static object FnSelect(string index, object listOfObjects)
            => new Dictionary<string, object> {
                ["Fn::Select"] = new List<object> {
                    index,
                    listOfObjects
                }
            };

        public static object FnSub(string input)
            => new Dictionary<string, object> {
                ["Fn::Sub"] = input
            };

        public static object FnSub(string input, IDictionary<string, object> variables) {

            // check if any variables have static values or !Ref expressions
            var staticVariables = variables.Select(kv => {
                string value = null;
                if(kv.Value is string text) {
                    value = text;
                } else if(TryGetFnRef(kv.Value, out string refKey)) {
                    value = $"${{{refKey}}}";
                } else if(TryGetFnGetAtt(kv.Value, out string getAttKey, out string getAttAttribute)) {
                    value = $"${{{getAttKey}.{getAttAttribute}}}";
                }
                return new {
                    Key = kv.Key,
                    Value = value
                };
            }).Where(kv => kv.Value is string).ToDictionary(kv => kv.Key, kv => kv.Value);
            if(staticVariables.Any()) {

                // substitute static variables
                foreach(var staticVariable in staticVariables) {
                    input = input.Replace($"${{{staticVariable.Key}}}", (string)staticVariable.Value);
                }
            }
            var remainingVariables = variables.Where(variable => !staticVariables.ContainsKey(variable.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

            // check which form of Fn::Sub to generate
            if(remainingVariables.Any()) {

                // return Fn:Sub with parameters
                return new Dictionary<string, object> {
                    ["Fn::Sub"] = new List<object> {
                        input,
                        remainingVariables
                    }
                };
            } else if(Regex.IsMatch(input, SUBVARIABLE_PATTERN)) {

                // return Fn:Sub with inline parameters
                return new Dictionary<string, object> {
                    ["Fn::Sub"] = input
                };
            } else {

                // return input string without any parameters
                return input;
            }
        }

        public static object FnSplit(string delimiter, object sourceString)
            => new Dictionary<string, object> {
                ["Fn::Split"] = new List<object> {
                    delimiter,
                    sourceString
                }
            };

        public static object FnEquals(object left, object right)
            => new Dictionary<string, object> {
                ["Fn::Equals"] = new List<object> {
                    left,
                    right
                }
            };

        public static object FnAnd(params object[] values)
            => new Dictionary<string, object> {
                ["Fn::And"] = values.ToList()
            };

        public static object FnOr(params object[] values)
            => new Dictionary<string, object> {
                ["Fn::Or"] = values.ToList()
            };

        public static object FnNot(object value)
            => new Dictionary<string, object> {
                ["Fn::Not"] = new List<object> {
                    value
                }
            };

        public static object FnCondition(string condition)
            => new Dictionary<string, object> {
                ["Condition"] = new List<object> {
                    condition
                }
            };

        public static string ReplaceSubPattern(string subPattern, Func<string, string, string> replace)
            => Regex.Replace(subPattern, SUBVARIABLE_PATTERN, match => {
                var matchText = match.ToString();
                var name = matchText.Substring(2, matchText.Length - 3).Trim().Split('.', 2);
                var suffix = (name.Length == 2) ? ("." + name[1]) : null;
                var key = name[0];
                return replace(key, suffix) ?? matchText;
            });

        public static bool TryGetFnRef(object value, out string key)  {
            if(
                (value is IDictionary<string, object> map)
                && (map.Count == 1)
                && map.TryGetValue("Ref", out object refObject)
                && (refObject is string refKey)
            ) {
                key = refKey;
                return true;
            }
            key = null;
            return false;
        }

        public static bool TryGetFnGetAtt(object value, out string key, out string attribute)  {
            if(
                (value is IDictionary<string, object> map)
                && (map.Count == 1)
                && map.TryGetValue("Fn::GetAtt", out object getAttObject)
                && (getAttObject is IList<object> getAttArgs)
                && (getAttArgs.Count == 2)
                && getAttArgs[0] is string getAttKey
                && getAttArgs[1] is string getAttAttribute
            ) {
                key = getAttKey;
                attribute = getAttAttribute;
                return true;
            }
            key = null;
            attribute = null;
            return false;
        }

        public static bool TryGetFnSub(object value, out string pattern, out IDictionary<string, object> arguments) {
            if(
                (value is IDictionary<string, object> map)
                && (map.Count == 1)
                && map.TryGetValue("Fn::Sub", out object subObject)
            ) {

                // determine which form of !Sub is being used
                if(subObject is string) {
                    pattern = (string)subObject;
                    arguments = new Dictionary<string, object>();
                    return true;
                }
                if(
                    (subObject is IList<object> subList)
                    && (subList.Count == 2)
                    && (subList[0] is string)
                    && (subList[1] is IDictionary<string, object>)
                ) {
                    pattern = (string)subList[0];
                    arguments = (IDictionary<string, object>)subList[1];
                    return true;
                }
            }
            pattern = null;
            arguments = null;
            return false;
       }
    }
}