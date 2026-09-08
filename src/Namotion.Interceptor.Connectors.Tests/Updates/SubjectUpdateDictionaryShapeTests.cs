using System.Collections;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateDictionaryShapeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenADictionaryUsesAPositionalCollectionDeclaration_ThenUpdateCreationRejectsTheShape(bool readOnly, bool partial)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new Person(context);
        var entries = new Dictionary<string, Person> { ["child"] = new() { FirstName = "Ada" } };
        ICollection children = readOnly ? new DictionaryCollectionView(entries) : entries;
        var property = root.TryGetRegisteredSubject()!.AddProperty(
            "RuntimeChildren", typeof(ICollection), _ => children, (_, value) => children = (ICollection)value!);
        var change = SubjectPropertyChange.Create<ICollection?>(
            property.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, children);

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() => partial
            ? SubjectUpdate.CreatePartialUpdateFromChanges(root, [change], [])
            : SubjectUpdate.CreateCompleteUpdate(root, []));
        Assert.Contains("dictionary", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenADictionaryUsesADictionaryDeclaration_ThenItsKeysAndChildrenRoundtrip()
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Relationships = new Dictionary<string, Person> { ["child"] = new() { FirstName = "Ada" } }
        };
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var propertyUpdate = update.Subjects[update.Root!][nameof(source.Relationships)];
        Assert.Equal(SubjectPropertyUpdateKind.Dictionary, propertyUpdate.Kind);
        Assert.Equal("child", Assert.Single(propertyUpdate.Items!).Index);
        Assert.Equal("Ada", Assert.Single(target.Relationships!).Value.FirstName);
        Assert.Equal("child", Assert.Single(target.TryGetRegisteredProperty(nameof(target.Relationships))!.Children).Index);
    }

    [Theory]
    [InlineData(typeof(IDictionary<string, Person>))]
    [InlineData(typeof(IReadOnlyDictionary<string, Person>))]
    [InlineData(typeof(PersonDictionary))]
    [InlineData(typeof(TaggedDictionary<int, string, Person>))]
    public void WhenADictionaryInterfaceIdentifiesItsKeyAndValueTypes_ThenACapableFactoryRoundtripsIt(Type declaredType)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var factory = new DictionaryShapeFactory();
        var sourceChildren = factory.CreateSubjectDictionary(declaredType,
            new Dictionary<object, IInterceptorSubject> { ["child"] = new Person { FirstName = "Ada" } });
        IDictionary? targetChildren = null;
        source.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", declaredType,
            _ => sourceChildren, (_, value) => sourceChildren = (IDictionary)value!);
        target.TryGetRegisteredSubject()!.AddProperty("RuntimeChildren", declaredType,
            _ => targetChildren, (_, value) => targetChildren = (IDictionary?)value);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, factory, ChangeOrigin.Local);

        // Assert
        Assert.True(declaredType.IsInstanceOfType(targetChildren));
        Assert.Equal("Ada", Assert.IsType<Person>(targetChildren!["child"]).FirstName);
        Assert.Equal("child", Assert.Single(target.TryGetRegisteredProperty("RuntimeChildren")!.Children).Index);
    }

    [Theory]
    [InlineData(typeof(PersonDictionary))]
    [InlineData(typeof(TaggedDictionary<int, string, Person>))]
    public void WhenTheDefaultDictionaryCannotSatisfyTheDeclaredType_ThenFactoryReportsTheUnsupportedShape(Type declaredType)
    {
        // Arrange
        var entries = new Dictionary<object, IInterceptorSubject> { ["child"] = new Person() };

        // Act & Assert
        var exception = Assert.Throws<NotSupportedException>(() =>
            DefaultSubjectFactory.Instance.CreateSubjectDictionary(declaredType, entries));
        Assert.Contains(nameof(ISubjectFactory), exception.Message);
    }

    private sealed class PersonDictionary : Dictionary<string, Person>;
    private sealed class TaggedDictionary<TTag, TKey, TValue> : Dictionary<TKey, TValue> where TKey : notnull;

    private sealed class DictionaryShapeFactory : ISubjectFactory
    {
        public IInterceptorSubject CreateSubject(Type type, IServiceProvider? serviceProvider) =>
            DefaultSubjectFactory.Instance.CreateSubject(type, serviceProvider);

        public IEnumerable<IInterceptorSubject?> CreateSubjectCollection(Type propertyType, params IEnumerable<IInterceptorSubject?> children) =>
            DefaultSubjectFactory.Instance.CreateSubjectCollection(propertyType, children);

        public IDictionary CreateSubjectDictionary(Type propertyType, IDictionary<object, IInterceptorSubject> entries)
        {
            IDictionary dictionary;
            if (propertyType == typeof(PersonDictionary)) dictionary = new PersonDictionary();
            else if (propertyType == typeof(TaggedDictionary<int, string, Person>)) dictionary = new TaggedDictionary<int, string, Person>();
            else return DefaultSubjectFactory.Instance.CreateSubjectDictionary(propertyType, entries);
            foreach (var entry in entries) dictionary.Add(entry.Key, entry.Value);
            return dictionary;
        }
    }

    private sealed class DictionaryCollectionView(Dictionary<string, Person> entries)
        : IReadOnlyDictionary<string, Person>, ICollection
    {
        public Person this[string key] => entries[key];
        public IEnumerable<string> Keys => entries.Keys;
        public IEnumerable<Person> Values => entries.Values;
        public int Count => entries.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public bool ContainsKey(string key) => entries.ContainsKey(key);
        public bool TryGetValue(string key, out Person value) => entries.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, Person>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void CopyTo(Array array, int index) => ((ICollection)entries).CopyTo(array, index);
    }
}
