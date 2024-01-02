using CodePlayground;
using System;
using System.Collections.Generic;

namespace Ragdoll
{
    internal abstract class Layer
    {
        public virtual void OnPushed() { }
        public virtual void OnPopped() { }

        public virtual void OnUpdate(double delta) { }
        public virtual void OnImGuiRender() { }

        public virtual void PreRender(Renderer renderer) { }
        public virtual void OnRender(Renderer renderer) { }
    }

    internal enum LayerType
    {
        Overlay,
        Layer
    }

    internal interface ILayerView
    {
        public T? FindLayer<T>() where T : Layer;
        public Layer? FindLayer(Type layerType);

        public void EnumerateLayers(Action<Layer> callback, LayerType? type = null);
        public int GetLayerCount(LayerType type);
    }

    internal sealed class LayerStack : ILayerView
    {
        public LayerStack()
        {
            var layerTypeValues = Enum.GetValues<LayerType>();
            mLayerTypeOffsets = new int[layerTypeValues.Length];
            Array.Fill(mLayerTypeOffsets, 0);

            mLayers = new List<Layer>();
        }

        public bool HasLayer<T>() where T : Layer => FindLayer<T>() is not null;
        public bool HasLayer(Type layerType) => FindLayer(layerType) is not null;

        public T? FindLayer<T>() where T : Layer => (T?)FindLayer(typeof(T));
        public Layer? FindLayer(Type layerType)
        {
            using var findEvent = Profiler.Event();
            if (!layerType.Extends(typeof(Layer)))
            {
                throw new ArgumentException("The passed type is not derived from Layer!");
            }

            foreach (var layer in mLayers)
            {
                if (layer.GetType() == layerType)
                {
                    return layer;
                }
            }

            return null;
        }

        public void EnumerateLayers(Action<Layer> callback, LayerType? type = null)
        {
            using var enumerateEvent = Profiler.Event();

            int nextOffset, count;
            if (type is not null)
            {
                var layerType = type.Value;
                count = GetLayerCount(layerType);
                nextOffset = GetOffset((int)layerType + 1);
            }
            else
            {
                nextOffset = count = mLayers.Count;
            }

            for (int i = 0; i < count; i++)
            {
                int index = nextOffset - (i + 1);
                var layer = mLayers[index];

                callback.Invoke(layer);
            }
        }

        public int GetLayerCount(LayerType type)
        {
            using var getCountEvent = Profiler.Event();
            int index = (int)type;

            int offset = GetOffset(index);
            int nextOffset = GetOffset(index + 1);

            return nextOffset - offset;
        }

        private int GetOffset(int layerType)
        {
            using var getOffsetEvent = Profiler.Event();
            return layerType < mLayerTypeOffsets.Length ? mLayerTypeOffsets[layerType] : mLayers.Count;
        }

        public void PushLayer<T>(LayerType type, params object?[] args) where T : Layer
        {
            using var pushEvent = Profiler.Event();

            var layer = Utilities.CreateDynamicInstance<T>(args);
            PushLayer(type, layer);
        }

        public void PushLayer(LayerType type, Layer layer)
        {
            using var pushEvent = Profiler.Event();
            int offsetIndex = (int)type;

            int index = GetOffset(offsetIndex);
            mLayers.Insert(index, layer);

            for (int i = offsetIndex + 1; i < mLayerTypeOffsets.Length; i++)
            {
                mLayerTypeOffsets[i]++;
            }

            layer.OnPushed();
        }

        public void PopLayer(LayerType type)
        {
            using var popEvent = Profiler.Event();
            int offsetIndex = (int)type;

            int index = GetOffset(offsetIndex);
            var layer = mLayers[index];
            layer.OnPopped();

            mLayers.RemoveAt(index);
            for (int i = offsetIndex + 1; i < mLayerTypeOffsets.Length; i++)
            {
                mLayerTypeOffsets[i]--;
            }
        }

        public void Clear()
        {
            using var clearEvent = Profiler.Event();
            foreach (var layer in mLayers)
            {
                layer.OnPopped();
            }

            mLayers.Clear();
            Array.Fill(mLayerTypeOffsets, 0);
        }

        private int[] mLayerTypeOffsets;
        private List<Layer> mLayers;
    }
}