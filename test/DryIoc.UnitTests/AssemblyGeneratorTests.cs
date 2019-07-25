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
