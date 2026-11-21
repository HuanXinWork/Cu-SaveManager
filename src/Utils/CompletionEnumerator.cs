using System;
using System.Collections;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>包装目标协程枚举器，在其自然迭代结束的首帧回调一次收尾动作。</summary>
    internal sealed class CompletionEnumerator : IEnumerator, IDisposable
    {
        private readonly IEnumerator _inner;
        private readonly Action _onCompleted;
        private bool _completed;

        internal CompletionEnumerator(IEnumerator inner, Action onCompleted)
        {
            _inner = inner;
            _onCompleted = onCompleted;
        }

        public object Current => _inner?.Current;

        public bool MoveNext()
        {
            bool hasNext = _inner != null && _inner.MoveNext();
            if (!hasNext && !_completed)
            {
                _completed = true;
                _onCompleted?.Invoke();
            }
            return hasNext;
        }

        public void Reset() => _inner?.Reset();

        public void Dispose() => (_inner as IDisposable)?.Dispose();
    }
}
