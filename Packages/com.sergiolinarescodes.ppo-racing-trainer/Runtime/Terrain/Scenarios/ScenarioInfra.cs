using System;
using System.Collections.Generic;
using Unidad.Core.EventBus;
using Unidad.Core.Factory;
using UnityEngine;

namespace UnityPpoRacingTrainer.Core.Terrain.Scenarios
{
    /// <summary>
    /// Minimal IEventBus for scenario use. The framework's <see cref="EventBus"/>
    /// is internal to its asmdef so we can't construct it directly from game code.
    /// </summary>
    internal sealed class ScenarioEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _subs = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            var t = typeof(T);
            if (!_subs.TryGetValue(t, out var list))
            {
                list = new List<Delegate>();
                _subs[t] = list;
            }
            list.Add(handler);
            return new Subscription(() => Unsubscribe(handler));
        }

        public void Publish<T>(T eventData) where T : struct
        {
            if (!_subs.TryGetValue(typeof(T), out var list)) return;
            foreach (var d in list.ToArray())
            {
                try { ((Action<T>)d).Invoke(eventData); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        public void Unsubscribe<T>(Action<T> handler) where T : struct
        {
            if (_subs.TryGetValue(typeof(T), out var list)) list.Remove(handler);
        }

        public void ClearAllSubscriptions() => _subs.Clear();

        private sealed class Subscription : IDisposable
        {
            private Action _onDispose;
            public Subscription(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
        }
    }

    /// <summary>
    /// Minimal IGameObjectFactory for scenario use. Tracks created GameObjects so
    /// the scenario can dispose them in cleanup.
    /// </summary>
    internal sealed class ScenarioGameObjectFactory : IGameObjectFactory
    {
        private readonly List<GameObject> _tracked = new();

        public GameObject CreatePrimitive(PrimitiveType type, string name, Vector3 position)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.position = position;
            _tracked.Add(go);
            return go;
        }

        public GameObject CreateEmpty(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            _tracked.Add(go);
            return go;
        }

        public GameObject InstantiatePrefab(string resourcePath, string name, Vector3 position)
        {
            var prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab == null) return null;
            var go = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            go.name = name;
            _tracked.Add(go);
            return go;
        }

        public void Destroy(GameObject obj)
        {
            if (obj == null) return;
            _tracked.Remove(obj);
            UnityEngine.Object.DestroyImmediate(obj);
        }

        public void DestroyAll()
        {
            foreach (var go in _tracked)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _tracked.Clear();
        }

        public void SetActive(GameObject obj, bool active)
        {
            if (obj != null) obj.SetActive(active);
        }

        public void SetColor(GameObject obj, Color color)
        {
            if (obj == null) return;
            var r = obj.GetComponent<Renderer>();
            if (r != null && r.sharedMaterial != null) r.sharedMaterial.color = color;
        }

        public void Dispose() => DestroyAll();
    }
}
