// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Microsoft.AspNetCore.Mvc.Abstractions
{
    /// <summary>
    /// Describes a method parameter.
    /// </summary>
    public class ParameterDescriptor
    {
        /// <summary>
        /// The name of the parameter.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// The type of the parameter.
        /// </summary>
        public Type ParameterType { get; set; }

        /// <summary>
        /// Metadata associated with the parameter.
        /// </summary>
        public BindingInfo BindingInfo { get; set; }
    }
}
