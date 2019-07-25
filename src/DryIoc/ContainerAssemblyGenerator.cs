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

            var rootsByType = generatedExpressions.Roots.GroupBy(r => KV.Of(r.Key.ServiceType, r.Key.ServiceKey))
                .ToLookup(g => g.Key);
            var dependenciesByType = generatedExpressions.ResolveDependencies
                .GroupBy(r => KV.Of(r.Key.ServiceType, r.Key.ServiceKey)).ToLookup(g => g.Key);

            var simpleFactories = rootsByType.Where(g => !dependenciesByType.Contains(g.Key));

            var aName = new AssemblyName(nameof(ContainerAssembly));
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(aName, AssemblyBuilderAccess.RunAndSave);
            var module = assembly.DefineDynamicModule(aName.Name);

            var generatedContainerVariable = Expression.Variable(typeof(Container));
            
            var registrationLines = new List<Expression>();
            foreach (var simpleFactory in simpleFactories.SelectMany(l => l).SelectMany(g => g))
            {
                GenerateSimpleFactory(simpleFactory, module, generatedContainerVariable, registrationLines);
            }
            
            var generatorType = module.DefineType(nameof(ContainerGenerator), TypeAttributes.Public);
            var generatorMethod = generatorType.DefineMethod(nameof(ContainerGenerator.GetContainer),
                MethodAttributes.Public | MethodAttributes.Static, typeof(Container), new Type[0]);

            var newContainerExpr = Expression.Assign(generatedContainerVariable, Expression.New(typeof(Container)));

            var block = Expression.Block(typeof(Container), new[] {generatedContainerVariable},
                new[] {newContainerExpr}.Concat(registrationLines).Concat(new[] {generatedContainerVariable}));
            var methodBody = Expression.Lambda<Container>(block);

            methodBody.CompileToMethod(generatorMethod);

            var callGenerator = generatorMethod.CreateDelegate(typeof(Func<Container>)) as Func<Container>;
            
            return new ContainerAssembly(assembly, callGenerator);
        }

        private static void GenerateSimpleFactory(KeyValuePair<ServiceInfo, Expression<FactoryDelegate>> simpleFactory,
            ModuleBuilder module, ParameterExpression generatedContainerVariable, List<Expression> registrationLines)
        {
            var typeName = GetTypeName(simpleFactory.Key, module);
            var factoryType = module.DefineType(typeName, TypeAttributes.Public, typeof(GeneratedFactoryBase));

            var factoryDelegateMethodInfo = typeof(FactoryDelegate).GetMethod(nameof(FactoryDelegate.Invoke));
            var factoryDelegateMethod = factoryType.DefineMethod(nameof(FactoryDelegate), MethodAttributes.Static,
                factoryDelegateMethodInfo.CallingConvention, factoryDelegateMethodInfo.ReturnType,
                factoryDelegateMethodInfo.GetParameters().Select(p => p.ParameterType).ToArray());
            simpleFactory.Value.CompileToMethod(factoryDelegateMethod);
            
            var getDelegate = factoryType.DefineMethod(nameof(GeneratedFactoryBase.GetDelegateOrDefault), MethodAttributes.Public,
                CallingConventions.Standard, typeof(FactoryDelegate), new[] {typeof(Request)});
            //Expression.MakeMemberAccess(Expression)
        }

        private static string GetTypeName(ServiceInfo serviceInfo, ModuleBuilder module)
        {
            var serviceTypeName = serviceInfo.ServiceType.FullName.Replace('.', '_');
            var keyValue = serviceInfo.ServiceKey?.ToString();
            var baseName = keyValue == null ? serviceTypeName : serviceTypeName + '_' + keyValue;
            for (var i = 0; ; i++)
            {
                var typeName = i == 0 ? baseName : baseName + '_' + i;
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
            GetGeneratedExpressions(container, reg => reg.AsResolutionRoot ? reg.ToServiceInfo().One() : null, null);

        /// <summary>
        /// 
        /// </summary>
        public abstract class GeneratedFactoryBase : Factory
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
        }

        /// <summary>
        /// 
        /// </summary>
        public abstract class GeneratedFactory : GeneratedFactoryBase
        {
            /// <summary>
            /// 
            /// </summary>
            /// <param name="request"></param>
            /// <returns></returns>
            public override FactoryDelegate GetDelegateOrDefault(Request request) => FactoryDelegates
                .FirstOrDefault(t => request.RequiredServiceType == t.Item1 && request.Parent.Equals(t.Item2))?.Item3;

            /// <summary>
            /// 
            /// </summary>
            protected IEnumerable<Tuple<Type, Request, FactoryDelegate>> FactoryDelegates { get; }
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
