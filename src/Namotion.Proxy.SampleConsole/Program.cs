﻿using Namotion.Proxy;
using Namotion.Proxy.Abstractions;

namespace ConsoleApp1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var context = ProxyContext
                .CreateBuilder()
                .AddHandler(new LogPropertyChangesHandler())
                .WithFullPropertyTracking()
                .Build();

            context
                .GetPropertyChangedObservable()
                .Subscribe((change) => 
                    Console.WriteLine($"Property {change.Property.Name} changed from {change.OldValue} to {change.NewValue}."));

            var child1 = new Person { FirstName = "Child1" };
            var child2 = new Person { FirstName = "Child2" };
            var child3 = new Person { FirstName = "Child3" };

            var person = new Person(context)
            {
                FirstName = "Rico",
                LastName = "Suter",
                Mother = new Person
                {
                    FirstName = "Susi"
                },
                Children = 
                [
                    child1,
                    child2
                ]
            };

            person.Children = 
            [
                child1,
                child2,
                child3
            ];

            person.Children = Array.Empty<Person>();

            Console.WriteLine(person.FirstName);
        }
    }

    [GenerateProxy]
    public partial class Person
    {
        public partial string FirstName { get; set; }

        public partial string? LastName { get; set; }

        [Derived]
        public string FullName => $"{FirstName} {LastName}";

        public partial Person? Father { get; set; }

        public partial Person? Mother { get; set; }

        public partial Person[] Children { get; set; }

        public Person()
        {
            Children = [];
        }

        public override string ToString()
        {
            return "Person: " + FullName;
        }
    }

    public class LogPropertyChangesHandler : IProxyLifecycleHandler
    {
        public void OnProxyAttached(ProxyLifecycleContext context)
        {
            Console.WriteLine($"Attach Proxy: {context.Proxy}");
        }

        public void OnProxyDetached(ProxyLifecycleContext context)
        {
            Console.WriteLine($"Detach Proxy: {context.Proxy}");
        }
    }
}
