using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using ImTools;

namespace DryIoc
{
    /// <summary>
    /// 
    /// </summary>
    public static class ContainerAssemblyGenerator
    {
        private static readonly MethodInfo _addRegistrationMethod = typeof(IRegistrator).GetMethod(nameof(IRegistrator.Register));
        private static readonly MethodInfo _factoryDelegateMethodInfo = typeof(FactoryDelegate).GetMethod(nameof(FactoryDelegate.Invoke));
        private static readonly ConstructorInfo _factoryDelegateConstructorInfo = typeof(FactoryDelegate).GetConstructor(new[] {typeof(object), typeof(IntPtr)});

        /// <summary>
        /// 
        /// </summary>
        /// <param name="container"></param>
        /// <param name="selectResolutionRoots"></param>
        /// <param name="resolutionRoots"></param>
        /// <returns></returns>
        public static ContainerAssembly GenerateContainerAssembly(this Container container,
            Func<ServiceRegistrationInfo, ServiceInfo[]> selectResolutionRoots = null,
            ServiceInfo[] resolutionRoots = null)
        {
            var generatedExpressions = GetGeneratedExpressions(container, selectResolutionRoots, resolutionRoots);

            var rootsByType = generatedExpressions.Roots.ToLookup(r => KV.Of(r.Key.ServiceType, r.Key.ServiceKey));
            var dependenciesByType = generatedExpressions.ResolveDependencies
                .ToLookup(r => KV.Of(r.Key.ServiceType, r.Key.ServiceKey));

            var allKeys = rootsByType.Select(g => g.Key).Union(dependenciesByType.Select(g => g.Key));

            var aName = new AssemblyName(nameof(ContainerAssembly));
            var fileName = aName + ".dll";
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(aName, AssemblyBuilderAccess.RunAndSave);
            var module = assembly.DefineDynamicModule(aName.Name, fileName);

            var generatedContainerVariable = Expression.Variable(typeof(Container));
            
            var registrationLines = new List<Expression>();
            foreach (var key in allKeys)
            {
                var rootRegistration = rootsByType[key].SingleOrDefault();
                var dependencyRegistrations = dependenciesByType[key].ToList();

                var factoryType = CreateFactoryType(module, key);

                if (rootRegistration.Key != null)
                {
                    AddRootFactoryDelegate(rootRegistration, factoryType);
                }

                if (dependencyRegistrations.Any())
                {
                    AddDependencyFactoryDelegates(dependencyRegistrations, factoryType);
                }

                factoryType.CreateType();

                var registrationLine = GenerateAddRegistrationLine(key, generatedContainerVariable, factoryType);
                registrationLines.Add(registrationLine);
            }

            var generatorType = module.DefineType(nameof(ContainerGenerator), TypeAttributes.Public);
            var generatorMethod = generatorType.DefineMethod(nameof(ContainerGenerator.GetContainer),
                MethodAttributes.Public | MethodAttributes.Static, typeof(Container), new Type[0]);

            var newContainerExpr = Expression.Assign(generatedContainerVariable, Expression.New(typeof(Container)));

            var block = Expression.Block(typeof(Container), new[] {generatedContainerVariable},
                new[] {newContainerExpr}.Concat(registrationLines).Concat(new[] {generatedContainerVariable}));
            var methodBody = Expression.Lambda<Func<Container>>(block);

            methodBody.CompileToMethod(generatorMethod);

            var concreteGeneratorType = generatorType.CreateType();
            
            assembly.Save(fileName);

            var concreteGeneratorMethod =
                concreteGeneratorType.GetMethod(generatorMethod.Name, BindingFlags.Public | BindingFlags.Static);
            concreteGeneratorMethod.ThrowIfNull();
            var callGenerator = concreteGeneratorMethod.CreateDelegate(typeof(Func<Container>)) as Func<Container>;
            
            return new ContainerAssembly(assembly, callGenerator);
        }

        private static void AddRootFactoryDelegate(KeyValuePair<ServiceInfo, Expression<FactoryDelegate>> rootFactory, TypeBuilder factoryType)
        {
            var factoryDelegateMethod = factoryType.DefineMethod("Root" + nameof(FactoryDelegate), MethodAttributes.Static,
                CallingConventions.Standard, _factoryDelegateMethodInfo.ReturnType,
                _factoryDelegateMethodInfo.GetParameters().Select(p => p.ParameterType).ToArray());
            rootFactory.Value.CompileToMethod(factoryDelegateMethod);

            var getDelegate = factoryType.DefineMethod(nameof(GeneratedFactory.GetDelegateOrDefault),
                MethodAttributes.Public | MethodAttributes.Virtual,
                CallingConventions.Standard, typeof(FactoryDelegate), new[] {typeof(Request)});
            var getDelegateIL = getDelegate.GetILGenerator();
            getDelegateIL.Emit(OpCodes.Ldnull);
            getDelegateIL.Emit(OpCodes.Ldftn, factoryDelegateMethod);
            getDelegateIL.Emit(OpCodes.Newobj, _factoryDelegateConstructorInfo);
            getDelegateIL.Emit(OpCodes.Ret);
        }

        private static void AddDependencyFactoryDelegates(List<KeyValuePair<Request,Expression>> dependencyRegistrations, TypeBuilder factoryType)
        {
            throw new NotImplementedException();
        }

        private static MethodCallExpression GenerateAddRegistrationLine(KV<Type, object> simpleFactory,
            ParameterExpression generatedContainerVariable, TypeBuilder factoryType)
        {
            var newFactoryExpression = Expression.New(factoryType);
            var registrationLine = Expression.Call(generatedContainerVariable, _addRegistrationMethod,
                newFactoryExpression, Expression.Constant(simpleFactory.Key, typeof(Type)),
                Expression.Constant(simpleFactory.Value, typeof(object)),
                Expression.Constant(null, typeof(IfAlreadyRegistered?)), Expression.Constant(false));
            return registrationLine;
        }

        private static TypeBuilder CreateFactoryType(ModuleBuilder module, KV<Type, object> serviceInfo)
        {
            var typeName = GetTypeName(serviceInfo, module);
            return module.DefineType(typeName, TypeAttributes.Public, typeof(GeneratedFactory));
        }

        private static string GetTypeName(KV<Type, object> serviceInfo, ModuleBuilder module)
        {
            var serviceTypeName = serviceInfo.Key.FullName.Replace(".", "");
            var keyValue = serviceInfo.Value?.ToString() ?? string.Empty;
            var baseName = serviceTypeName + keyValue + "Factory";
            for (var i = 0; ; i++)
            {
                var typeName = i == 0 ? baseName : baseName + i;
                if (!module.GetTypes().Any(t => t.Name.Equals(typeName, StringComparison.InvariantCultureIgnoreCase)))
                    return typeName;
            }
        }

        private static ContainerTools.GeneratedExpressions GetGeneratedExpressions(Container container,
            Func<ServiceRegistrationInfo, ServiceInfo[]> selectResolutionRoots, ServiceInfo[] resolutionRoots) =>
            selectResolutionRoots == null && resolutionRoots == null
                ? GetAllRegistrationsAsResolutionRoots(container)
                : container.GenerateResolutionExpressions(regs =>
                    regs.SelectMany(r => (selectResolutionRoots?.Invoke(r)).EmptyIfNull())
                        .Concat(resolutionRoots.EmptyIfNull()));

        private static ContainerTools.GeneratedExpressions GetAllRegistrationsAsResolutionRoots(Container container) =>
            GetGeneratedExpressions(container, reg => reg.ToServiceInfo().One(), null);

        /// <summary>
        /// 
        /// </summary>
        public abstract class GeneratedFactory : Factory
        {
            /// <summary>
            /// 
            /// </summary>
            /// <param name="request"></param>
            /// <returns></returns>
            public override FastExpressionCompiler.LightExpression.Expression CreateExpressionOrDefault(Request request) =>
                throw new NotImplementedException();

            /// <summary>
            /// 
            /// </summary>
            /// <param name="_"></param>
            /// <returns></returns>
            public override bool UseInterpretation(Request _) => false;

            internal override bool ValidateAndNormalizeRegistration(Type serviceType, object serviceKey,
                bool isStaticallyChecked, Rules rules) => true;

            /// <summary>
            /// 
            /// </summary>
            /// <param name="request"></param>
            /// <returns></returns>
            public override FactoryDelegate GetDelegateOrDefault(Request request)
            {
                var factoryDelegates = GetDependencyFactoryDelegates();
                var selectedDependencyFactory = factoryDelegates.FirstOrDefault(t =>
                    request.RequiredServiceType == t.Item1 && request.Parent.Equals(t.Item2));

                return selectedDependencyFactory?.Item3 ?? GetRootFactoryDelegate();
            }

            /// <summary>
            /// 
            /// </summary>
            /// <returns></returns>
            protected virtual FactoryDelegate GetRootFactoryDelegate() => null;

            /// <summary>
            /// 
            /// </summary>
            protected virtual IEnumerable<Tuple<Type, Request, FactoryDelegate>> GetDependencyFactoryDelegates() =>
                new Tuple<Type, Request, FactoryDelegate>[0];
        }
    }

    internal static class ContainerGenerator
    {
        public static Container GetContainer() => throw new NotImplementedException();
    }
    
    /// <summary>
    /// 
    /// </summary>
    public class ContainerAssembly
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="assembly"></param>
        /// <param name="containerFactory"></param>
        public ContainerAssembly(AssemblyBuilder assembly, Func<Container> containerFactory)
        {
            Assembly = assembly;
            ContainerFactory = containerFactory;
        }

        /// <summary>
        /// 
        /// </summary>
        public AssemblyBuilder Assembly { get; }
        /// <summary>
        /// 
        /// </summary>
        public Func<Container> ContainerFactory { get; }
    }
}
