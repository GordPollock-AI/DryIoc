using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NUnit.Framework;

namespace DryIoc.UnitTests
{
    [TestFixture]
    public class AssemblyGeneratorTests
    {
        [Test]
        public void InspectMethodGroupExpression()
        {
            var expected = RE(() => FactoryDelegateMethod);

            var factoryDelegateMethodInfo = typeof(AssemblyGeneratorTests).GetMethod(nameof(FactoryDelegateMethod),
                BindingFlags.Static | BindingFlags.NonPublic);
            var specificExpected = RE(() =>
                (FactoryDelegate) Delegate.CreateDelegate(typeof(AssemblyGeneratorTests), factoryDelegateMethodInfo));

            Console.WriteLine(expected);
            Console.WriteLine(specificExpected);
            
            Assert.That(specificExpected, Is.EqualTo(expected));
            
            var createDelegateMethod = typeof(Delegate).GetMethod(nameof(Delegate.CreateDelegate), new[]{typeof(Type), typeof(MethodInfo)});
            var manualExpression =
                Expression.Lambda<Func<FactoryDelegate>>(
                    Expression.Convert(
                        Expression.Constant(
                        Expression.Call(createDelegateMethod, Expression.Constant(typeof(AssemblyGeneratorTests)),
                            Expression.Constant(factoryDelegateMethodInfo))),
                    typeof(FactoryDelegate)));

            Console.WriteLine(manualExpression);
            
            Assert.That(manualExpression, Is.EqualTo(expected));
        }
        
        private static Container FactoryDelegateMethod(IResolverContext _) => throw new NotImplementedException();

        private Expression RE(Expression<Func<FactoryDelegate>> e) => e;
    }
}