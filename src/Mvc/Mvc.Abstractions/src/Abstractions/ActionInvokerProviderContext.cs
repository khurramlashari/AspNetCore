// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.AspNetCore.Mvc.Abstractions
{
    /// <summary>
    /// The context used by the providers for ActionInvokers.
    /// </summary>
    public class ActionInvokerProviderContext
    {
        /// <summary>
        /// Constructs the <see cref="ActionInvokerProviderContext"/>.
        /// </summary>
        /// <param name="actionContext">The context under which to invoke the action.</param>
        public ActionInvokerProviderContext(ActionContext actionContext)
        {
            if (actionContext == null)
            {
                throw new ArgumentNullException(nameof(actionContext));
            }

            ActionContext = actionContext;
        }

        /// <summary>
        /// The context under which to invoke the action.
        /// </summary>
        public ActionContext ActionContext { get; }

        /// <summary>
        /// The result of invoking the action.
        /// </summary>
        public IActionInvoker Result { get; set; }
    }
}
