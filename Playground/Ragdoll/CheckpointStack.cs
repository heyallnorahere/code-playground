using CodePlayground.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Ragdoll
{
    public sealed class CheckpointStack
    {
        private static int sCurrentID;
        static CheckpointStack()
        {
            sCurrentID = 0;
        }

        private sealed class CheckpointEvent : IDisposable
        {
            public CheckpointEvent(string value, int id, CheckpointStack stack)
            {
                mValue = value;
                mID = id;
                mStack = stack;

                if (mStack.mEnable)
                {
                    mStack.Event(mValue);
                }
            }

            public void Dispose()
            {
                if (!mStack.mEnable)
                {
                    return;
                }

                if (mStack.mEvents.Peek().mID != mID)
                {
                    throw new InvalidOperationException($"Scope mismatch!");
                }

                mStack.mEvents.Pop();
                mStack.Event(mValue + '~');
            }

            public string Value => mValue;

            private readonly string mValue;
            private readonly int mID;
            private readonly CheckpointStack mStack;
        }

        public CheckpointStack(bool enable)
        {
            mEnable = enable;
            mEvents = new Stack<CheckpointEvent>();
            mCurrentList = null;
        }

        public void SetCommandList(ICommandList commandList)
        {
            mCurrentList = commandList;
        }

        [MemberNotNull(nameof(mCurrentList))]
        private void VerifyCommandList()
        {
            if (mCurrentList is not null)
            {
                return;
            }

            throw new InvalidOperationException("No command list set!");
        }

        public IDisposable Push(string name)
        {
            var result = new CheckpointEvent(name, sCurrentID++, this);
            mEvents.Push(result);
            return result;
        }

        public void Event(string name)
        {
            string identifier = CreateIdentifier(name);
            PushCheckpoint(identifier);
        }

        private void PushCheckpoint(string name)
        {
            VerifyCommandList();
            mCurrentList.Checkpoint(name);
        }

        private string CreateIdentifier(string name)
        {
            var names = new List<string>();
            names.Add(name);
            names.AddRange(mEvents.Select(checkpointEvent => checkpointEvent.Value));

            var identifier = string.Empty;
            foreach (var scopeName in names)
            {
                var prepend = scopeName;
                if (identifier.Length > 0)
                {
                    prepend += '/';
                }

                identifier = prepend + identifier;
            }

            return identifier;
        }

        private readonly Stack<CheckpointEvent> mEvents;
        private readonly bool mEnable;
        private ICommandList? mCurrentList;
    }
}