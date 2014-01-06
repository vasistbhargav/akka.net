﻿using Pigeon.Messaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Pigeon.Actor
{
    public partial class ActorCell : IActorContext, IActorRefFactory
    {
        public virtual ActorSystem System { get; set; }
        internal Props Props { get;private set; }
        internal LocalActorRef Self { get;private set; }
        internal IActorContext Parent { get;private set; }
        internal ActorBase Actor { get; set; }
        private Message CurrentMessage { get; set; }
        public ActorRef Sender { get;private set; }
        internal Receive CurrentBehavior { get; private set; }
        private Stack<Receive> behaviorStack = new Stack<Receive>();
        private Mailbox Mailbox { get; set; }
        private HashSet<ActorRef> Watchees = new HashSet<ActorRef>();
        [ThreadStatic]
        private static ActorCell current;
        internal static ActorCell Current
        {
            get
            {
                return current;
            }
        }

        protected ConcurrentDictionary<string, LocalActorRef> Children = new ConcurrentDictionary<string, LocalActorRef>();

        public virtual LocalActorRef Child(string name)
        {
            LocalActorRef actorRef = null;
            Children.TryGetValue(name, out actorRef);
            return actorRef;
        }

        public virtual ActorRef ActorSelection(string actorPath)
        {
            return ActorSelection(new ActorPath(actorPath));
        }

        public ActorSelection ActorSelection(ActorPath actorPath)
        {
            //remote path
            if (actorPath.First.StartsWith("pigeon."))
            {
                var actorRef = new RemoteActorRef(this, actorPath);
                return new ActorSelection(actorRef);
            }

            //local absolute
            if (actorPath.First.StartsWith("pigeon:"))
            {
                actorPath = new ActorPath(actorPath.Skip(1));
            }

            //standard
            var currentContext = this;
            foreach (var part in actorPath)
            {
                if (part == "..")
                {
                    currentContext = ((ActorCell)currentContext.Parent);
                }
                else if (part == "." || part == "")
                {
                    currentContext = currentContext.System;
                }
                else if (part == "*")
                {
                    var actorRef = new ActorSelection(currentContext.Children.Values.ToArray());
                    return actorRef;
                }
                else
                {
                    currentContext = ((LocalActorRef)this.Child(part)).Cell;
                }
            }
            
            return new ActorSelection( ((ActorCell)currentContext).Self);
        }

        public virtual LocalActorRef ActorOf<TActor>(string name = null)
        {
            return ActorOf(new Props(typeof(TActor)), name);
        }

        public virtual LocalActorRef ActorOf(Props props, string name = null)
        {
            if (name == null)
            {
                name = props.Type.Name;
                if (name.EndsWith("Actor"))
                    name = name.Substring(0, name.Length - 5);

                name = name + "#" + Guid.NewGuid();
            }

            var cell = new ActorCell(this,props, name);

            NewActor(cell);
            return cell.Self;
        }

        protected void NewActor(ActorCell cell)
        {
            //set the thread static context or things will break
            cell.UseThreadContext( () =>
            {
                var instance = cell.Props.NewActor();
                instance.AroundPreStart();
                Children.TryAdd(cell.Self.Path.Name, cell.Self);
            });
        }

        /// <summary>
        /// May be called from anyone
        /// </summary>
        /// <returns></returns>
        public IEnumerable<LocalActorRef> GetChildren()
        {
            return this.Children.Values.ToArray();
        }

        protected ActorCell()
        {
        }

        internal ActorCell(IActorContext parent, Props props, string name)
        {
            this.Parent = parent;
            this.System = parent != null ? parent.System : null;
            this.Self = new LocalActorRef(new ActorPath(name), this);
            this.Props = props;
            this.Mailbox = new BufferBlockMailbox();
            this.Mailbox.OnNext = this.OnNext;
        }

        internal void UseThreadContext(Action action)
        {
            var tmp = Current;
            current = this;
            try
            {
                action();
            }
            finally
            {
                //ensure we set back the old context
                current = tmp;
            }
        }


        public void Become(Receive receive)
        {
            behaviorStack.Push(receive);
            CurrentBehavior = receive;
        }
        public void Unbecome()
        {
            CurrentBehavior = behaviorStack.Pop(); ;
        }
        public void OnNext(Message message)
        {
            this.CurrentMessage = message;
            this.Sender = message.Sender;
            //set the current context
            UseThreadContext(() =>
            {
                OnReceiveInternal(message.Payload);
            });
        }
        internal void Post(ActorRef sender, LocalActorRef target, object message)
        {
            var m = new Message
            {
                Sender = sender,
                Target = target,
                Payload = message,
            };
            Mailbox.Post(m);
        }

        /// <summary>
        /// May only be called from the owner actor
        /// </summary>
        /// <param name="subject"></param>
        public void Watch(ActorRef subject)
        {
            Watchees.Add(subject);
            subject.Tell(new Watch());
        }

        /// <summary>
        /// May only be called from the owner actor
        /// </summary>
        /// <param name="subject"></param>
        public void Unwatch(ActorRef subject)
        {
            Watchees.Remove(subject);
            subject.Tell(new Unwatch());
        }
    }
}
