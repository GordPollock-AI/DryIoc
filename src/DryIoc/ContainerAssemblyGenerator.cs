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
        private static readonly ConstructorInfo _generatedFactoryConstructor = typeof(GeneratedFactory).GetConstructors().Single();
        private static readonly ConstructorInfo _dependencyFactorySelectorConstructor = typeof(Tuple<Type, Request, FactoryDelegate>).GetConstructors().Single();

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

            var rootsByType = generatedExpressions.Roots.ToLookup(r => ToServiceTypeKeyPair(r.Key));
            var dependenciesByType = generatedExpressions.ResolveDependencies
                .ToLookup(r => ToServiceTypeKeyPair(r.Key));

            var allKeys = rootsByType.Select(g => g.Key).Union(dependenciesByType.Select(g => g.Key));

            var aName = new AssemblyName(nameof(ContainerAssembly));
            var fileName = aName + ".dll";
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(aName, AssemblyBuilderAccess.RunAndSave);
            var module = assembly.DefineDynamicModule(aName.Name, fileName);

            var generatorType = module.DefineType(nameof(ContainerGenerator), TypeAttributes.Public);
            var generatorMethod = generatorType.DefineMethod(nameof(ContainerGenerator.GetContainer),
                MethodAttributes.Public | MethodAttributes.Static, typeof(Container), new Type[0]);
            var generatorTypeMethodNames = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
                {nameof(ContainerGenerator.GetContainer)};

            var generatedContainerVariable = Expression.Variable(typeof(Container));
            
            var registrationLines = new List<Expression>();
            foreach (var key in allKeys)
            {
                var rootFactoryDelegate = CreateRootFactoryDelegateExpression(rootsByType[key], generatorType, generatorTypeMethodNames);

                var dependencyFactoryDelegates = dependenciesByType[key].Select(d =>
                    CreateDependencyFactoryDelegateExpression(d, container, generatorType, generatorTypeMethodNames));
                var dependencyDelegatesArray = Expression.NewArrayInit(typeof(Tuple<Type, Request, FactoryDelegate>), dependencyFactoryDelegates);

                var delegateArguments = new[] {rootFactoryDelegate, dependencyDelegatesArray};

                var registrationLine =
                    GenerateAddRegistrationLine(key, generatedContainerVariable, delegateArguments);
                registrationLines.Add(registrationLine);
            }


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

        private static KV<Type, object> ToServiceTypeKeyPair(ServiceInfo serviceInfo) =>
            KV.Of(serviceInfo.ServiceType, serviceInfo.ServiceKey);

        private static KV<Type, object> ToServiceTypeKeyPair(Request request) =>
            KV.Of(request.ServiceType, request.ServiceKey);

        private static Expression CreateRootFactoryDelegateExpression(
            IEnumerable<KeyValuePair<ServiceInfo, Expression<FactoryDelegate>>> rootRegistrations,
            TypeBuilder generatorType, ISet<string> generatorTypeMethodNames)
        {
            var rootRegistration = rootRegistrations.SingleOrDefault();
            if (rootRegistration.Key == null)
                return Expression.Constant(null, typeof(FactoryDelegate));

            var methodName = GetMethodName(ToServiceTypeKeyPair(rootRegistration.Key), generatorTypeMethodNames);
            var method = CreateFactoryMethod(generatorType, methodName, rootRegistration.Value);
            return GeneratedFactory.GetFactoryDelegateExpression(method);
        }

        private static string GetMethodName(KV<Type, object> serviceTypeKey, ISet<string> methodNames)
        {
            var serviceTypeName = serviceTypeKey.Key.FullName.Replace(".", "");
            var keyValue = serviceTypeKey.Value?.ToString() ?? string.Empty;
            var baseName = "Construct" + serviceTypeName + keyValue;
            for (var i = 0; ; i++)
            {
                var methodName = i == 0 ? baseName : baseName + i;
                if (methodNames.Add(methodName))
                {
                    return methodName;
                }
            }
        }

        private static MethodInfo CreateFactoryMethod(TypeBuilder generatorType, string methodName, Expression<FactoryDelegate> factoryExpression)
        {
            var method = generatorType.DefineMethod(methodName, MethodAttributes.Private | MethodAttributes.Static);
            factoryExpression.CompileToMethod(method);
            return method;
        }

        private static Expression CreateDependencyFactoryDelegateExpression(
            KeyValuePair<Request, Expression> dependencyInfo,
            Container container,
            TypeBuilder generatorType,
            HashSet<string> generatorTypeMethodNames)
        {
            var typeExpression = Expression.Constant(dependencyInfo.Key.RequiredServiceType, typeof(Type));

            var requestExpression = container.GetRequestExpression(dependencyInfo.Key).ToExpression();

            var methodName = GetMethodName(ToServiceTypeKeyPair(dependencyInfo.Key), generatorTypeMethodNames);
            var method = CreateFactoryMethod(generatorType, methodName, dependencyInfo.Value.WrapInFactoryExpression());
            var factoryDelegateExpression = GeneratedFactory.GetFactoryDelegateExpression(method);
            
            return Expression.New(_dependencyFactorySelectorConstructor, typeExpression, requestExpression, factoryDelegateExpression);
        }

        /// <summary>Wraps service creation expression (body) into <see cref="FactoryDelegate"/> and returns result lambda expression.</summary>
        private static Expression<FactoryDelegate> WrapInFactoryExpression(this Expression expression) =>
            Expression.Lambda<FactoryDelegate>(expression.NormalizeExpression(),
                FactoryDelegateCompiler.ResolverContextParamExpr.ToParameterExpression());
        
        /// Strips the unnecessary or adds the necessary cast to expression return result
        private static Expression NormalizeExpression(this Expression expr)
        {
            if (expr.NodeType == ExpressionType.Convert)
            {
                var operandExpr = ((UnaryExpression)expr).Operand;
                if (operandExpr.Type.IsAssignableTo(expr.Type))
                    return operandExpr;
            }

            return expr.Type != typeof(void) && expr.Type.IsValueType() ? Expression.Convert(expr, typeof(object)) : expr;
        }

        private static MethodCallExpression GenerateAddRegistrationLine(KV<Type, object> simpleFactory,
            ParameterExpression generatedContainerVariable,
            IEnumerable<Expression> delegateArguments)
        {
            return Expression.Call(generatedContainerVariable, _addRegistrationMethod,
                Expression.New(_generatedFactoryConstructor, delegateArguments),
                Expression.Constant(simpleFactory.Key, typeof(Type)),
                Expression.Constant(simpleFactory.Value, typeof(object)),
                Expression.Constant(null, typeof(IfAlreadyRegistered?)),
                Expression.Constant(false));
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
        public class GeneratedFactory : Factory
        {
            private readonly FactoryDelegate _rootFactoryDelegate;
            private readonly Tuple<Type, Request, FactoryDelegate>[] _dependencyFactoryDelegates;

            /// <summary>
            /// 
            /// </summary>
            /// <param name="rootFactoryDelegate"></param>
            /// <param name="dependencyFactoryDelegates"></param>
            public GeneratedFactory(FactoryDelegate rootFactoryDelegate,
                params Tuple<Type, Request, FactoryDelegate>[] dependencyFactoryDelegates)
            {
                _rootFactoryDelegate = rootFactoryDelegate;
                _dependencyFactoryDelegates = dependencyFactoryDelegates;
            }

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
                var selectedDependencyFactory = _dependencyFactoryDelegates.FirstOrDefault(t =>
                    request.RequiredServiceType == t.Item1 && request.Parent.Equals(t.Item2));

                return selectedDependencyFactory?.Item3 ?? _rootFactoryDelegate;
            }

            /// <summary>
            /// 
            /// </summary>
            private static readonly Expression<Func<FactoryDelegate>> _getFactoryDelegateTemplateExpression =
                () => FactoryDelegateTemplate;

            private static object FactoryDelegateTemplate(IResolverContext _) => throw new NotImplementedException();

            /// <summary>
            /// 
            /// </summary>
            /// <param name="factoryMethod"></param>
            /// <returns></returns>
            public static Expression GetFactoryDelegateExpression(MethodInfo factoryMethod)
            {
                var visitor = new FactoryDelegateExpressionBuilder(factoryMethod.ThrowIfNull());
                var factoryDelegateDelegate = (Expression<Func<FactoryDelegate>>) visitor.Visit(_getFactoryDelegateTemplateExpression);
                return factoryDelegateDelegate.Body;
            }

            private class FactoryDelegateExpressionBuilder : ExpressionVisitor
            {
                private readonly MethodInfo _factoryMethod;

                public FactoryDelegateExpressionBuilder(MethodInfo factoryMethod)
                {
                    _factoryMethod = factoryMethod;
                }

                protected override Expression VisitConstant(ConstantExpression node) =>
                    node.Type == typeof(MethodInfo) ? Expression.Constant(_factoryMethod, typeof(MethodInfo)) : node;
            }
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
