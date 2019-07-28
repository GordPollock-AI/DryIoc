using System;
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

            var factoryDelegateExpression =
                ContainerAssemblyGenerator.GetFactoryDelegateExpression(testFactoryMethod);

            Expression<Func<FactoryDelegate>> expectedDelegateDelegate = () => TestFactory.FactoryDelegate;
            var expected = expectedDelegateDelegate.Body;
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
