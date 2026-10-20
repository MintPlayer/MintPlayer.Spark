using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Shipping.Models;

namespace MintPlayer.Spark.Commerce.Shipping.Services;

/// <summary>
/// Who sees which shipments: the <c>Read/Shipment</c> right (the <c>commerce-shipping:staff</c> slot), narrowed by
/// every registered <see cref="IShipmentRowFilter"/>. Used by the query, the custom actions and the label endpoints.
/// </summary>
internal sealed class ShipmentAccess(IPermissionService permissions, IServiceProvider services)
{
    /// <summary>The combined row filter, or null when no filter narrows the rows.</summary>
    public async Task<Expression<Func<Shipment, bool>>?> RowFilterAsync(CancellationToken cancellationToken)
    {
        Expression<Func<Shipment, bool>>? combined = null;
        foreach (var filter in services.GetServices<IShipmentRowFilter>())
        {
            if (await filter.GetFilterAsync(cancellationToken) is { } next)
                combined = combined is null ? next : And(combined, next);
        }
        return combined;
    }

    /// <summary>Whether the caller may read <paramref name="shipment"/>.</summary>
    public async Task<bool> CanReadAsync(Shipment shipment, CancellationToken cancellationToken)
    {
        if (!await permissions.IsAllowedAsync("Read", nameof(Shipment), cancellationToken))
            return false;
        return await RowFilterAsync(cancellationToken) is not { } filter || filter.Compile()(shipment);
    }

    private static Expression<Func<Shipment, bool>> And(Expression<Func<Shipment, bool>> left, Expression<Func<Shipment, bool>> right)
    {
        var parameter = left.Parameters[0];
        var body = new Rebind(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<Shipment, bool>>(Expression.AndAlso(left.Body, body), parameter);
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
