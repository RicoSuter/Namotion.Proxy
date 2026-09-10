using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Connectors.Tests.Transactions;

/// <summary>
/// Tests for transaction behavior when property values have type mismatches.
///
/// KEY FINDING: Type errors throw IMMEDIATELY in both cases because the generated
/// SetValue delegate casts the value BEFORE calling the property setter:
///   (o, v) => ((Person)o).FirstName = (string)v  // Cast fails before setter
///
/// This is actually the DESIRED behavior - fail fast, don't buffer invalid values.
/// </summary>
public class SubjectTransactionTypeErrorTests
{
    [Fact]
    public void WithoutTransaction_WrongType_ThrowsImmediately()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "John" };

        // Act & Assert - passing an int for a string property should throw immediately
        // The cast in the generated SetValue delegate fails before the setter is called
        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            person.UpdatePropertyValuesFromPaths(
                ["FirstName"],
                DateTimeOffset.UtcNow,
                (property, path) => 42, // Wrong type: int instead of string
                DefaultPathProvider.Instance,
                source: null);
        });

        Assert.IsType<InvalidCastException>(exception);

        // The property value should NOT have changed
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task WithTransaction_WrongType_AlsoThrowsImmediately()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context) { FirstName = "John" };

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            cancellationToken: CancellationToken.None);

        // Act & Assert - ALSO throws immediately because the cast happens
        // in the SetValue delegate BEFORE the transaction interceptor sees it
        var exception = Assert.ThrowsAny<Exception>(() =>
        {
            person.UpdatePropertyValuesFromPaths(
                ["FirstName"],
                DateTimeOffset.UtcNow,
                (property, path) => 42, // Wrong type: int instead of string
                DefaultPathProvider.Instance,
                source: null);
        });

        Assert.IsType<InvalidCastException>(exception);

        // The property value should NOT have changed
        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task WithTransaction_DirectPropertySet_WorksWithCorrectType()
    {
        // Arrange - Verify that direct property setting works with transactions
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context);
        Assert.Equal(123, person.FirstName_MaxLength); // from constructor

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            cancellationToken: CancellationToken.None);

        // Direct property set with correct type
        person.FirstName_MaxLength = 456;

        // Value is buffered, not yet committed
        Assert.Equal(456, person.FirstName_MaxLength); // reads from transaction buffer

        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(456, person.FirstName_MaxLength);
    }

    [Fact]
    public async Task WithTransaction_CorrectTypes_CommitsSuccessfully()
    {
        // Arrange - verify that correct types work fine
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithTransactions()
            .WithFullPropertyTracking();

        var person = new Person(context)
        {
            FirstName = "John",
            LastName = "Doe"
        };

        using var transaction = await context.BeginTransactionAsync(
            TransactionFailureHandling.BestEffort,
            cancellationToken: CancellationToken.None);

        // Update with correct types
        person.UpdatePropertyValuesFromPaths(
            ["FirstName", "LastName"],
            DateTimeOffset.UtcNow,
            (property, path) => path == "FirstName" ? "Jane" : "Smith",
            DefaultPathProvider.Instance,
            source: null);

        // Act - should commit successfully
        await transaction.CommitAsync(CancellationToken.None);

        // Assert
        Assert.Equal("Jane", person.FirstName);
        Assert.Equal("Smith", person.LastName);
    }
}
