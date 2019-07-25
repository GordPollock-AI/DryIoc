using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using DryIoc.UnitTests.CUT;
using NUnit.Framework;

namespace DryIoc.UnitTests
{
    [TestFixture]
    public class AssemblyGeneratorTests
    {
        private class TestFactory
        {
            public static object FactoryDelegate(IResolverContext _) => nameof(TestFactory);
        }

        [Test]
        public void GetFactoryDelegateExpression_GetsCorrectDelegateExpression()
        {
            var testFactoryMethod = typeof(TestFactory).GetMethod(nameof(FactoryDelegate), BindingFlags.Static | BindingFlags.Public);

            Console.WriteLine(ContainerAssemblyGenerator.GeneratedFactoryBase.GetFactoryDelegateTemplateExpression);
            Expression<Func<FactoryDelegate>> expected = () => TestFactory.FactoryDelegate;
            Console.WriteLine(expected);
            
            var factoryDelegateExpression =
                ContainerAssemblyGenerator.GeneratedFactoryBase.GetFactoryDelegateExpression(testFactoryMethod);

            Console.WriteLine(factoryDelegateExpression);
            
            Assert.That(factoryDelegateExpression.ToString(), Is.EqualTo(expected.ToString()));
        }
        
        [Test]
        public void Resolving_service_should_return_registered_implementation()
        {
            var container = new Container();
            container.Register(typeof(IService), typeof(Service));
            
            var generatedAssembly = container.GenerateContainerAssembly();
            var newContainer = generatedAssembly.ContainerFactory();

            var service = newContainer.Resolve(typeof(IService));

            Assert.IsInstanceOf<Service>(service);
        }
    }
}
