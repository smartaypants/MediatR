namespace MediatR.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.IO;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Shouldly;
    using StructureMap;
    using StructureMap.Graph;
    using Xunit;
    using StructureMap.Pipeline;
    using Autofac;
    using Autofac.Features.Variance;
    using Autofac.Core;

    public class CustomNotificationTests
    {
        public class CustomNotification : CustomNotificationBase<CustomRequest, CustomResponse>
        {
            public CustomNotification(CustomRequest request) : base(request)
            {
            }
        }

        public abstract class CustomNotificationBase<TRequest, TResponse> : ICustomNotification
            where TRequest : CustomRequestBase<TResponse>, new()
        {
            private readonly TRequest _request;

            public CustomNotificationBase(TRequest request)
            {
                _request = request;
            }

            public IStrategy Strategy => new SendMoreRequestsStrategy<TRequest, TResponse>(_request, 2); //send 2 more requests
        }

        public class SendMoreRequestsStrategy<TRequest, TResponse> : IStrategy
            where TRequest : CustomRequestBase<TResponse>, new()
        {
            private readonly TRequest _request;
            private readonly int _numberOfRequests;

            public SendMoreRequestsStrategy(TRequest request, int numberOfRequests)
            {
                _request = request;
                _numberOfRequests = numberOfRequests;
            }

            public virtual void SendMoreRequests(IMediator mediator)
            {
                var request = new TRequest() { Message = _request.Message + _numberOfRequests.ToString() };
                mediator.Send(request);
            }
        }

        public abstract class CustomRequestBase<TResponse> : IRequest<TResponse>
        {
            public string Message { get; set; }
        }

        public class CustomResponse
        {
            public string Message { get; set; }
        }

        public class CustomRequest : CustomRequestBase<CustomResponse>
        {
        }

        public class CustomRequestHandler : CustomRequestHandlerBase<CustomRequest, CustomResponse>
        {
            private readonly TextWriter _writer;

            public CustomRequestHandler(TextWriter writer)
            {
                _writer = writer;
            }

            protected override CustomResponse HandleCore(CustomRequest request)
            {
                _writer.WriteLine(request.Message);
                var response = new CustomResponse() { Message = request.Message };
                return response;
            }
        }

        public abstract class CustomRequestHandlerBase<TRequest, TResponse> : RequestHandler<TRequest, TResponse>
            where TRequest : CustomRequestBase<TResponse>
        {
        }

        public interface IStrategy
        {
            void SendMoreRequests(IMediator mediator);
        }

        public interface ICustomNotification : INotification
        {
            IStrategy Strategy { get; }
        }

        public class CustomNotificationHandler<TNotification> : AsyncNotificationHandler<TNotification>
            where TNotification : class, ICustomNotification
        {
            protected readonly IMediator _mediator;

            public CustomNotificationHandler(IMediator mediator)
            {
                _mediator = mediator;
            }
            protected override Task HandleCore(TNotification notification)
            {
                return Task.Run(() => notification.Strategy.SendMoreRequests(_mediator));
            }
        }

        [Fact]
        public async Task Should_resolve_main_handler()
        {
            var sb = new StringBuilder();
            var writer = new StringWriter(sb);

            var mediator = FromAutofac(writer);
            //var mediator = FromStructureMap(writer);

            await mediator.Publish(new CustomNotification(new CustomRequest() { Message = "Ping" }));

            var result = sb.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            result.ShouldContain("Ping2");
        }

        private IMediator FromAutofac(StringWriter writer)
        {
            var builder = new ContainerBuilder();

            // Autofac setup: https://github.com/jbogard/MediatR/wiki
            // enables contravariant Resolve() for interfaces with single contravariant ("in") arg
            builder
              .RegisterSource(new ContravariantRegistrationSource());

            // mediator itself
            builder
              .RegisterType<Mediator>()
              .As<IMediator>()
              .InstancePerLifetimeScope();

            // request handlers
            builder
              .Register<SingleInstanceFactory>(ctx => {
                  var c = ctx.Resolve<IComponentContext>();
                  return t => { object o; return c.TryResolve(t, out o) ? o : null; };
              })
              .InstancePerLifetimeScope();

            // notification handlers
            builder
              .Register<MultiInstanceFactory>(ctx => {
                  var c = ctx.Resolve<IComponentContext>();
                  return t => (IEnumerable<object>)c.Resolve(typeof(IEnumerable<>).MakeGenericType(t));
              })
              .InstancePerLifetimeScope();

            var mediatrOpenTypes = new[]
            {
                typeof(IRequestHandler<,>),
                typeof(IRequestHandler<>),
                typeof(INotificationHandler<>),
            };

            foreach (var mediatrOpenType in mediatrOpenTypes)
            {
                builder
                    .RegisterAssemblyTypes(typeof(CustomNotification).GetTypeInfo().Assembly)
                    .AsClosedTypesOf(mediatrOpenType)
                    .AsImplementedInterfaces();
            }

            builder.RegisterGeneric(typeof(CustomNotificationHandler<>)).As(typeof(INotificationHandler<>));

            builder.RegisterInstance(writer).As<TextWriter>();

            var container = builder.Build();

            var mediator = container.Resolve<IMediator>();

            return mediator;
        }

        private IMediator FromStructureMap(StringWriter writer)
        {
            var container = new StructureMap.Container(cfg =>
            {
                cfg.Scan(scanner =>
                {
                    scanner.AssemblyContainingType<CustomNotification>();
                    scanner.AssemblyContainingType<IMediator>();
                    scanner.WithDefaultConventions();
                    scanner.AddAllTypesOf(typeof(IRequestHandler<,>));
                    scanner.AddAllTypesOf(typeof(INotificationHandler<>));
                });
                //cfg.Scan(scanner =>
                //{
                //    scanner.AssemblyContainingType<CustomNotification>();
                //    scanner.ConnectImplementationsToTypesClosing(typeof(IRequestHandler<,>));
                //    scanner.ConnectImplementationsToTypesClosing(typeof(IRequestHandler<>));
                //    scanner.ConnectImplementationsToTypesClosing(typeof(INotificationHandler<>));
                //});

                //Constrained notification handlers
                cfg.For(typeof(INotificationHandler<>)).Add(typeof(CustomNotificationHandler<>));

                // This is the default but let's be explicit. At most we should be container scoped.
                cfg.For<IMediator>().LifecycleIs<TransientLifecycle>().Use<Mediator>();

                cfg.For<SingleInstanceFactory>().Use<SingleInstanceFactory>(ctx => ctx.GetInstance);
                cfg.For<MultiInstanceFactory>().Use<MultiInstanceFactory>(ctx => ctx.GetAllInstances);
                cfg.For<TextWriter>().Use(writer);
            });

            var mediator = container.GetInstance<IMediator>();

            return mediator;
        }
    }
}