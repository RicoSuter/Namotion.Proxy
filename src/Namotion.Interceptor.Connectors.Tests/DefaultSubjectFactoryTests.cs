using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests;

public class DefaultSubjectFactoryTests
{
    public class MyClass : IInterceptorSubject
    {
        public object Injected { get; }
        
        public MyClass(object injected)
        {
            Injected = injected;
        }
        
        IInterceptorExecutor IInterceptorSubject.Executor => null!;

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = null!;

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = null!;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
        {
            throw new NotImplementedException();
        }
    }
    
    [Fact]
    public void WhenCreatingSubject_ThenServiceProviderIsUsed()
    {
        // Arrange
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<object>(42);

        var context = InterceptorSubjectContext.Create().WithLifecycle();
        context.AddService(serviceCollection.BuildServiceProvider());

        var person = new Person(context);
        var property = new RegisteredSubjectProperty(
            new RegisteredSubject(person),
            nameof(Person.Mother),
            typeof(MyClass),
            []);

        // Act
        var subjectFactory = new DefaultSubjectFactory();
        var myClass = subjectFactory.CreateSubject(property) as MyClass;
        
        // Assert
        Assert.NotNull(myClass);
        Assert.Equal(42, myClass.Injected);
    }
    
    [Fact]
    public void WhenCreatingSubjectCollection_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();

        var person = new Person(context);
        var property = new RegisteredSubjectProperty(
            new RegisteredSubject(person),
            nameof(Person.Mother),
            typeof(IList<MyClass>),
            []);

        // Act
        var subjectFactory = new DefaultSubjectFactory();
        var myClassCollection = subjectFactory.CreateSubjectCollection(property.Type, new MyClass(1), new MyClass(2)) as IList<MyClass>;

        // Assert
        Assert.NotNull(myClassCollection);
        Assert.Equal(2, myClassCollection.Count());
    }

    [Fact]
    public void WhenCreatingDictionaryWithCorrectKeyType_ThenNoConversionNeeded()
    {
        // Arrange
        var entries = new Dictionary<object, IInterceptorSubject>
        {
            ["key1"] = new MyClass(1),
            ["key2"] = new MyClass(2)
        };

        // Act
        var subjectFactory = new DefaultSubjectFactory();
        var dictionary = subjectFactory.CreateSubjectDictionary(
            typeof(Dictionary<string, MyClass>), entries);

        // Assert
        Assert.Equal(2, dictionary.Count);
        Assert.Equal(1, ((MyClass)dictionary["key1"]!).Injected);
        Assert.Equal(2, ((MyClass)dictionary["key2"]!).Injected);
    }

    [Fact]
    public void WhenCreatingDictionaryWithIntKeyFromLong_ThenKeysAreConverted()
    {
        // Arrange
        var entries = new Dictionary<object, IInterceptorSubject>
        {
            [1L] = new MyClass("a"),
            [2L] = new MyClass("b")
        };

        // Act
        var subjectFactory = new DefaultSubjectFactory();
        var dictionary = subjectFactory.CreateSubjectDictionary(
            typeof(Dictionary<int, MyClass>), entries);

        // Assert
        Assert.Equal(2, dictionary.Count);
        Assert.Equal("a", ((MyClass)dictionary[1]!).Injected);
        Assert.Equal("b", ((MyClass)dictionary[2]!).Injected);
    }

    public enum TestKeyEnum { First, Second }

    [Fact]
    public void WhenCreatingDictionaryWithEnumKey_ThenKeysAreConverted()
    {
        // Arrange
        var entries = new Dictionary<object, IInterceptorSubject>
        {
            ["First"] = new MyClass(1),
            ["Second"] = new MyClass(2)
        };

        // Act
        var subjectFactory = new DefaultSubjectFactory();
        var dictionary = subjectFactory.CreateSubjectDictionary(
            typeof(Dictionary<TestKeyEnum, MyClass>), entries);

        // Assert
        Assert.Equal(2, dictionary.Count);
        Assert.Equal(1, ((MyClass)dictionary[TestKeyEnum.First]!).Injected);
        Assert.Equal(2, ((MyClass)dictionary[TestKeyEnum.Second]!).Injected);
    }
}