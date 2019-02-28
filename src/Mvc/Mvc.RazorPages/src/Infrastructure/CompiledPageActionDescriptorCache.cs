// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure
{
    internal class CompiledPageActionDescriptorCache
    {
        private readonly IPageApplicationModelProvider[] _applicationModelProviders;
        private readonly IViewCompilerProvider _viewCompilerProvider;
        private readonly ActionEndpointFactory _endpointFactory;
        private readonly PageConventionCollection _conventions;
        private readonly FilterCollection _globalFilters;
        private readonly IActionDescriptorCollectionProvider _collectionProvider;
        private readonly IPageFactoryProvider _pageFactoryProvider;
        private readonly IPageModelFactoryProvider _modelFactoryProvider;
        private readonly IModelBinderFactory _modelBinderFactory;
        private readonly IRazorPageFactoryProvider _razorPageFactoryProvider;
        private volatile InnerCache _currentCache;
        private readonly ParameterBinder _parameterBinder;
        private readonly IModelMetadataProvider _modelMetadataProvider;

        public CompiledPageActionDescriptorCache(
            IActionDescriptorCollectionProvider collectionProvider,
            IEnumerable<IPageApplicationModelProvider> applicationModelProviders,
            IViewCompilerProvider viewCompilerProvider,
            ActionEndpointFactory endpointFactory,
            IPageFactoryProvider pageFactoryProvider,
            IPageModelFactoryProvider modelFactoryProvider,
            IRazorPageFactoryProvider razorPageFactoryProvider,
            ParameterBinder parameterBinder,
            IModelMetadataProvider modelMetadataProvider,
            IModelBinderFactory modelBinderFactory,
            IOptions<RazorPagesOptions> pageOptions,
            IOptions<MvcOptions> mvcOptions)
        {
            _applicationModelProviders = applicationModelProviders
                .OrderBy(p => p.Order)
                .ToArray();

            _viewCompilerProvider = viewCompilerProvider;
            _endpointFactory = endpointFactory;
            _conventions = pageOptions.Value.Conventions;
            _globalFilters = mvcOptions.Value.Filters;

            _collectionProvider = collectionProvider;
            _pageFactoryProvider = pageFactoryProvider;
            _modelFactoryProvider = modelFactoryProvider;
            _modelBinderFactory = modelBinderFactory;
            _razorPageFactoryProvider = razorPageFactoryProvider;
            _parameterBinder = parameterBinder;
            _modelMetadataProvider = modelMetadataProvider;
        }

        private IViewCompiler Compiler => _viewCompilerProvider.GetCompiler();

        private InnerCache Cache
        {
            get
            {
                var current = _currentCache;
                var actionDescriptors = _collectionProvider.ActionDescriptors;

                if (current == null || current.Version != actionDescriptors.Version)
                {
                    current = new InnerCache(actionDescriptors.Version);
                    _currentCache = current;
                }

                return current;
            }
        }

        public virtual ValueTask<CompiledPageActionDescriptor> GetOrAddAsync(PageActionDescriptor actionDescriptor)
        {
            var cache = Cache;
            if (cache.Entries.TryGetValue(actionDescriptor, out var compiledActionDescriptor))
            {
                return new ValueTask<CompiledPageActionDescriptor>(compiledActionDescriptor);
            }

            return CreateCacheEntryAsync(cache, actionDescriptor);
        }

        private async ValueTask<CompiledPageActionDescriptor> CreateCacheEntryAsync(InnerCache innerCache, PageActionDescriptor actionDescriptor)
        {
            var viewDescriptor = await Compiler.CompileAsync(actionDescriptor.RelativePath);
            var compiledActionDescriptor = GetCompiledPageActionDescriptor(actionDescriptor, viewDescriptor);
            var cacheEntry = CreateCacheEntry(compiledActionDescriptor);
            compiledActionDescriptor.PageActionInvokerCacheEntry = cacheEntry;

            innerCache.Entries.TryAdd(actionDescriptor, compiledActionDescriptor);
            return compiledActionDescriptor;
        }

        private PageActionInvokerCacheEntry CreateCacheEntry(CompiledPageActionDescriptor compiledActionDescriptor)
        {
            var viewDataFactory = ViewDataDictionaryFactory.CreateFactory(compiledActionDescriptor.DeclaredModelTypeInfo);

            var pageFactory = _pageFactoryProvider.CreatePageFactory(compiledActionDescriptor);
            var pageDisposer = _pageFactoryProvider.CreatePageDisposer(compiledActionDescriptor);
            var propertyBinder = PageBinderFactory.CreatePropertyBinder(
                _parameterBinder,
                _modelMetadataProvider,
                _modelBinderFactory,
                compiledActionDescriptor);

            Func<PageContext, object> modelFactory = null;
            Action<PageContext, object> modelReleaser = null;
            if (compiledActionDescriptor.ModelTypeInfo != compiledActionDescriptor.PageTypeInfo)
            {
                modelFactory = _modelFactoryProvider.CreateModelFactory(compiledActionDescriptor);
                modelReleaser = _modelFactoryProvider.CreateModelDisposer(compiledActionDescriptor);
            }

            var viewStartFactories = GetViewStartFactories(compiledActionDescriptor);

            var handlerExecutors = GetHandlerExecutors(compiledActionDescriptor);
            var handlerBinders = GetHandlerBinders(compiledActionDescriptor);

            return new PageActionInvokerCacheEntry(
                viewDataFactory,
                pageFactory,
                pageDisposer,
                modelFactory,
                modelReleaser,
                propertyBinder,
                handlerExecutors,
                handlerBinders,
                viewStartFactories);
        }

        // Internal for testing.
        internal List<Func<IRazorPage>> GetViewStartFactories(CompiledPageActionDescriptor descriptor)
        {
            var viewStartFactories = new List<Func<IRazorPage>>();
            // Always pick up all _ViewStarts, including the ones outside the Pages root.
            foreach (var filePath in RazorFileHierarchy.GetViewStartPaths(descriptor.RelativePath))
            {
                var factoryResult = _razorPageFactoryProvider.CreateFactory(filePath);
                if (factoryResult.Success)
                {
                    viewStartFactories.Insert(0, factoryResult.RazorPageFactory);
                }
            }

            return viewStartFactories;
        }

        private static PageHandlerExecutorDelegate[] GetHandlerExecutors(CompiledPageActionDescriptor actionDescriptor)
        {
            if (actionDescriptor.HandlerMethods == null || actionDescriptor.HandlerMethods.Count == 0)
            {
                return Array.Empty<PageHandlerExecutorDelegate>();
            }

            var results = new PageHandlerExecutorDelegate[actionDescriptor.HandlerMethods.Count];

            for (var i = 0; i < actionDescriptor.HandlerMethods.Count; i++)
            {
                results[i] = ExecutorFactory.CreateExecutor(actionDescriptor.HandlerMethods[i]);
            }

            return results;
        }

        private PageHandlerBinderDelegate[] GetHandlerBinders(CompiledPageActionDescriptor actionDescriptor)
        {
            if (actionDescriptor.HandlerMethods == null || actionDescriptor.HandlerMethods.Count == 0)
            {
                return Array.Empty<PageHandlerBinderDelegate>();
            }

            var results = new PageHandlerBinderDelegate[actionDescriptor.HandlerMethods.Count];

            for (var i = 0; i < actionDescriptor.HandlerMethods.Count; i++)
            {
                results[i] = PageBinderFactory.CreateHandlerBinder(
                    _parameterBinder,
                    _modelMetadataProvider,
                    _modelBinderFactory,
                    actionDescriptor,
                    actionDescriptor.HandlerMethods[i]);
            }

            return results;
        }

        private CompiledPageActionDescriptor GetCompiledPageActionDescriptor(PageActionDescriptor actionDescriptor, CompiledViewDescriptor viewDescriptor)
        {
            var context = new PageApplicationModelProviderContext(actionDescriptor, viewDescriptor.Type.GetTypeInfo());
            for (var i = 0; i < _applicationModelProviders.Length; i++)
            {
                _applicationModelProviders[i].OnProvidersExecuting(context);
            }

            for (var i = _applicationModelProviders.Length - 1; i >= 0; i--)
            {
                _applicationModelProviders[i].OnProvidersExecuted(context);
            }

            ApplyConventions(_conventions, context.PageApplicationModel);

            var compiled = CompiledPageActionDescriptorBuilder.Build(context.PageApplicationModel, _globalFilters);

            // We need to create an endpoint for routing to use and attach it to the CompiledPageActionDescriptor...
            // routing for pages is two-phase. First we perform routing using the route info - we can do this without
            // compiling/loading the page. Then once we have a match we load the page and we can create an endpoint
            // with all of the information we get from the compiled action descriptor.
            var endpoints = new List<Endpoint>();
            _endpointFactory.AddEndpoints(endpoints, compiled, Array.Empty<ConventionalRouteEntry>(), Array.Empty<Action<EndpointBuilder>>());

            // In some test scenarios there's no route so the endpoint isn't created. This is fine because
            // it won't happen for real.
            compiled.Endpoint = endpoints.SingleOrDefault();

            return compiled;
        }

        internal static void ApplyConventions(
            PageConventionCollection conventions,
            PageApplicationModel pageApplicationModel)
        {
            var applicationModelConventions = GetConventions<IPageApplicationModelConvention>(pageApplicationModel.HandlerTypeAttributes);
            foreach (var convention in applicationModelConventions)
            {
                convention.Apply(pageApplicationModel);
            }

            var handlers = pageApplicationModel.HandlerMethods.ToArray();
            foreach (var handlerModel in handlers)
            {
                var handlerModelConventions = GetConventions<IPageHandlerModelConvention>(handlerModel.Attributes);
                foreach (var convention in handlerModelConventions)
                {
                    convention.Apply(handlerModel);
                }

                var parameterModels = handlerModel.Parameters.ToArray();
                foreach (var parameterModel in parameterModels)
                {
                    var parameterModelConventions = GetConventions<IParameterModelBaseConvention>(parameterModel.Attributes);
                    foreach (var convention in parameterModelConventions)
                    {
                        convention.Apply(parameterModel);
                    }
                }
            }

            var properties = pageApplicationModel.HandlerProperties.ToArray();
            foreach (var propertyModel in properties)
            {
                var propertyModelConventions = GetConventions<IParameterModelBaseConvention>(propertyModel.Attributes);
                foreach (var convention in propertyModelConventions)
                {
                    convention.Apply(propertyModel);
                }
            }

            IEnumerable<TConvention> GetConventions<TConvention>(
                IReadOnlyList<object> attributes)
            {
                return Enumerable.Concat(
                    conventions.OfType<TConvention>(),
                    attributes.OfType<TConvention>());
            }
        }

        private class InnerCache
        {
            public InnerCache(int version)
            {
                Version = version;
            }

            public ConcurrentDictionary<PageActionDescriptor, CompiledPageActionDescriptor> Entries { get; } =
                new ConcurrentDictionary<PageActionDescriptor, CompiledPageActionDescriptor>();

            public int Version { get; }
        }
    }
}
