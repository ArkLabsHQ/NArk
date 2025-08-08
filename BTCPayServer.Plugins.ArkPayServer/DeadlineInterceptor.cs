using Grpc.Core;
using Grpc.Core.Interceptors;

namespace BTCPayServer.Plugins.ArkPayServer;

class DeadlineInterceptor(TimeSpan deadline) : Interceptor
{
    private void ApplyDeadline<TRequest, TResponse>(ref ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        if (context.Options.Deadline is null)
        {
            context = new(context.Method, context.Host, context.Options.WithDeadline(DateTime.UtcNow.Add(deadline)));
        }
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ApplyDeadline(ref context);
        return base.AsyncUnaryCall(request, context, continuation);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        ApplyDeadline(ref context);
        return base.BlockingUnaryCall(request, context, continuation);
    }
}