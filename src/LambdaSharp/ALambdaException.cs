﻿/*
 * MindTouch λ#
 * Copyright (C) 2018-2019 MindTouch, Inc.
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

namespace LambdaSharp {

    public abstract class ALambdaException : Exception, ILambdaExceptionFingerprinter {

        //--- Fields ---
        private readonly string _messageFormat;

        //--- Constructors ---
        protected ALambdaException(string format, params object[] args)
            : base(string.Format(format, args))
            => _messageFormat = format;

        protected ALambdaException(Exception innerException, string format, params object[] args)
            : base(string.Format(format, args), innerException ?? new ArgumentNullException(nameof(innerException)))
            => _messageFormat = format;

        //--- ILambdaExceptionFingerprinter Members ---
        string ILambdaExceptionFingerprinter.FingerprintValue => GetType().ToString() + _messageFormat;
    }
}
