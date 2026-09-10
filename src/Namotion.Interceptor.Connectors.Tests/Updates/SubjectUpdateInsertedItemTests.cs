using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// An update may insert an item and carry that item's properties in the same payload, addressing it
/// by index or key rather than by the insert's id. The item is then created and populated within one
/// apply, so the population must wait for the assignment exactly as the insert's own does.
/// </summary>
public class SubjectUpdateInsertedItemTests
{
    [Fact]
    public void WhenACollectionUpdateInsertsAnItemAndAlsoAddressesItByIndex_ThenTheItemIsPopulated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(context);

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    [nameof(Person.Children)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = 0,
                                Id = "inserted"
                            }
                        ],
                        Items = [new SubjectPropertyItemUpdate { Index = 0, Id = "properties" }]
                    }
                },
                ["inserted"] = new(),
                ["properties"] = new()
                {
                    [nameof(Person.FirstName)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Bob"
                    }
                }
            }
        };

        // Act
        var exception = Record.Exception(() => target.ApplySubjectUpdate(
            update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Null(exception);
        Assert.Single(target.Children);
        Assert.Equal("Bob", target.Children[0].FirstName);
    }

    [Fact]
    public void WhenADictionaryUpdateInsertsAnItemAndAlsoAddressesItByKey_ThenTheItemIsPopulated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(context);

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    [nameof(Person.Relationships)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Dictionary,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = "a",
                                Id = "inserted"
                            }
                        ],
                        Items = [new SubjectPropertyItemUpdate { Index = "a", Id = "properties" }]
                    }
                },
                ["inserted"] = new(),
                ["properties"] = new()
                {
                    [nameof(Person.FirstName)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Bob"
                    }
                }
            }
        };

        // Act
        var exception = Record.Exception(() => target.ApplySubjectUpdate(
            update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Null(exception);
        Assert.NotNull(target.Relationships);
        Assert.Equal("Bob", target.Relationships["a"].FirstName);
    }
}
