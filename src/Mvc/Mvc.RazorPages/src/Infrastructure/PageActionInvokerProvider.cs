// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure
{
    internal class PageActionInvokerProvider : IActionInvokerProvider
    {
        private readonly CompiledPageActionDescriptorCache _actionDescriptorCache;
        private readonly IFilterProvider[] _filterProviders;
        private readonly IReadOnlyList<IValueProviderFactory> _valueProviderFactories;
        private readonly ParameterBinder _parameterBinder;
        private readonly IModelMetadataProvider _modelMetadataProvider;
        private readonly ITempDataDictionaryFactory _tempDataFactory;
        private readonly MvcOptions _mvcOptions;
        private readonly HtmlHelperOptions _htmlHelperOptions;
        private readonly IPageHandlerMethodSelector _selector;
        private readonly DiagnosticListener _diagnosticListener;
        private readonly ILogger<PageActionInvoker> _logger;
        private readonly IActionResultTypeMapper _mapper;
        private readonly IActionContextAccessor _actionContextAccessor;

        public PageActionInvokerProvider(
            CompiledPageActionDescriptorCache actionDescriptorCache,
            IEnumerable<IFilterProvider> filterProviders,
            ParameterBinder parameterBinder,
            IModelMetadataProvider modelMetadataProvider,
            ITempDataDictionaryFactory tempDataFactory,
            IOptions<MvcOptions> mvcOptions,
            IOptions<HtmlHelperOptions> htmlHelperOptions,
            IPageHandlerMethodSelector selector,
            DiagnosticListener diagnosticListener,
            ILoggerFactory loggerFactory,
            IActionResultTypeMapper mapper)
            : this(
                actionDescriptorCache,
                filterProviders,
                parameterBinder,
                modelMetadataProvider,
                tempDataFactory,
                mvcOptions,
                htmlHelperOptions,
                selector,
                diagnosticListener,
                loggerFactory,
                mapper,
                actionContextAccessor: null)
        {
        }

        public PageActionInvokerProvider(
            CompiledPageActionDescriptorCache actionDescriptorCache,
            IEnumerable<IFilterProvider> filterProviders,
            ParameterBinder parameterBinder,
            IModelMetadataProvider modelMetadataProvider,
            ITempDataDictionaryFactory tempDataFactory,
            IOptions<MvcOptions> mvcOptions,
            IOptions<HtmlHelperOptions> htmlHelperOptions,
            IPageHandlerMethodSelector selector,
            DiagnosticListener diagnosticListener,
            ILoggerFactory loggerFactory,
            IActionResultTypeMapper mapper,
            IActionContextAccessor actionContextAccessor)
        {
            _actionDescriptorCache = actionDescriptorCache;
            _filterProviders = filterProviders.ToArray();
            _valueProviderFactories = mvcOptions.Value.ValueProviderFactories.ToArray();
            _parameterBinder = parameterBinder;
            _modelMetadataProvider = modelMetadataProvider;
            _tempDataFactory = tempDataFactory;
            _mvcOptions = mvcOptions.Value;
            _htmlHelperOptions = htmlHelperOptions.Value;
            _selector = selector;
            _diagnosticListener = diagnosticListener;
            _logger = loggerFactory.CreateLogger<PageActionInvoker>();
            _mapper = mapper;
            _actionContextAccessor = actionContextAccessor ?? ActionContextAccessor.Null;
        }

        public int Order { get; } = -1000;

        public void OnProvidersExecuting(ActionInvokerProviderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var actionContext = context.ActionContext;
            var actionDescriptor = actionContext.ActionDescriptor as PageActionDescriptor;
            if (actionDescriptor == null)
            {
                return;
            }

            CompiledPageActionDescriptor compiledPageActionDescriptor;
            if (_mvcOptions.EnableEndpointRouting)
            {
                // With endpoint routing, PageLoaderMatcherPolicy should have already produced a CompiledPageActionDescriptor instance.
                compiledPageActionDescriptor = (CompiledPageActionDescriptor)actionDescriptor;
            }
            else
            {
                var entryTask = _actionDescriptorCache.GetOrAddAsync(actionDescriptor);
                // Once the ActionDescriptor is created, the returned ValueTask should finish synchronously until the cache is invalidated.
                // Awaiting it here isn't entirely terrible.
                compiledPageActionDescriptor = entryTask.GetAwaiter().GetResult();
            }

            var cacheEntry = compiledPageActionDescriptor.PageActionInvokerCacheEntry;
            IFilterMetadata[] filters;
            if (cacheEntry.CacheableFilters == null)
            {
                var filterFactoryResult = FilterFactory.GetAllFilters(_filterProviders, actionContext);
                filters = filterFactoryResult.Filters;

                cacheEntry.CacheableFilters = filterFactoryResult.CacheableFilters;
            }
            else
            {
                filters = FilterFactory.CreateUncachedFilters(
                    _filterProviders,
                    actionContext,
                    cacheEntry.CacheableFilters);
            }

            context.Result = CreateActionInvoker(actionContext, compiledPageActionDescriptor, filters);
        }

        public void OnProvidersExecuted(ActionInvokerProviderContext context)
        {
        }

        private PageActionInvoker CreateActionInvoker(
            ActionContext actionContext,
            CompiledPageActionDescriptor actionDescriptor,
            IFilterMetadata[] filters)
        {
            var cacheEntry = actionDescriptor.PageActionInvokerCacheEntry;

            var pageContext = new PageContext(actionContext)
            {
                ActionDescriptor = actionDescriptor,
                ValueProviderFactories = new CopyOnWriteList<IValueProviderFactory>(_valueProviderFactories),
                ViewData = cacheEntry.ViewDataFactory(_modelMetadataProvider, actionContext.ModelState),
                ViewStartFactories = cacheEntry.ViewStartFactories.ToList(),
            };

            return new PageActionInvoker(
                _selector,
                _diagnosticListener,
                _logger,
                _actionContextAccessor,
                _mapper,
                pageContext,
                filters,
                cacheEntry,
                _parameterBinder,
                _tempDataFactory,
                _htmlHelperOptions);
        }
    }
}
