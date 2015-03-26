﻿using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace DryIoc.IssuesTests
{
    [TestFixture]
    public class Issue110_HidingMultipleContravariantImplementationsBehindComposite
    {
        [Test]
        public void Test_with_event_raiser_and_static_handle_counters()
        {
            var container = new Container();

            container.RegisterMany(new[] { typeof(IEventHandler<>).Assembly }, 
                type => type.GetGenericDefinitionOrNull() == typeof(IEventHandler<>));

            container.Register(typeof(IEventRaiser<>), typeof(EventRaiser<>));

            Assert.AreEqual(0, CustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(0, NotifyStaffWhenCustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(0, CustomerMovedAbroadEventHandler.HandleCount);

            container.Resolve<IEventRaiser<CustomerMovedEvent>>()
                .Raise(new CustomerMovedEvent());

            Assert.AreEqual(1, CustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(1, NotifyStaffWhenCustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(0, CustomerMovedAbroadEventHandler.HandleCount);

            container.Resolve<IEventRaiser<CustomerMovedAbroadEvent>>()
                .Raise(new CustomerMovedAbroadEvent());

            Assert.AreEqual(2, CustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(2, NotifyStaffWhenCustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(1, CustomerMovedAbroadEventHandler.HandleCount);

            container.Resolve<IEventRaiser<SpecialCustomerMovedEvent>>()
                .Raise(new SpecialCustomerMovedEvent());

            Assert.AreEqual(3, CustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(3, NotifyStaffWhenCustomerMovedEventHandler.HandleCount);
            Assert.AreEqual(1, CustomerMovedAbroadEventHandler.HandleCount);
        }

        public interface IEventRaiser<TEvent>
        {
            void Raise(TEvent e);
        }

        public class EventRaiser<TEvent> : IEventRaiser<TEvent>
        {
            List<IEventHandler<TEvent>> handlers;

            public EventRaiser(IEnumerable<IEventHandler<TEvent>> handlers)
            {
                this.handlers = handlers.ToList();
            }

            public void Raise(TEvent e)
            {
                handlers.ForEach(h => h.Handle(e));
            }
        }

        // Events:
        public class CustomerMovedEvent { }

        public class CustomerMovedAbroadEvent : CustomerMovedEvent { }

        public class SpecialCustomerMovedEvent : CustomerMovedEvent { }

        // Event handler definition (note the 'in' keyword):
        public interface IEventHandler<in TEvent>
        {
            void Handle(TEvent e);
        }

        // Event handler implementations:
        public class CustomerMovedEventHandler
            : IEventHandler<CustomerMovedEvent>
        {
            public static int HandleCount;
            public void Handle(CustomerMovedEvent e) { ++HandleCount; }
        }

        public class NotifyStaffWhenCustomerMovedEventHandler
            : IEventHandler<CustomerMovedEvent>
        {
            public static int HandleCount;
            public void Handle(CustomerMovedEvent e) { ++HandleCount; }
        }

        public class CustomerMovedAbroadEventHandler
            : IEventHandler<CustomerMovedAbroadEvent>
        {
            public static int HandleCount;
            public void Handle(CustomerMovedAbroadEvent e) { ++HandleCount; }
        }

        // A composite wrapping possibly multiple handlers.
        public class MultipleDispatchEventHandler<TEvent>
            : IEventHandler<TEvent>
        {
            private readonly IEnumerable<IEventHandler<TEvent>> _handlers;

            public MultipleDispatchEventHandler(IEventHandler<TEvent>[] handlers)
            {
                _handlers = handlers;
            }

            public void Handle(TEvent e)
            {
                // Ignore because it does not affect test
                //_handlers.ToList().ForEach(h => h.Handle(e));
            }
        }
    }
}