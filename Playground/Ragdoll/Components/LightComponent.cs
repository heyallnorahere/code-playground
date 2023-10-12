using System;
using System.Numerics;
using ImGuiNET;
using Ragdoll.Layers;

namespace Ragdoll.Components
{
    [RegisteredComponent(DisplayName = "Light source")]
    public sealed class LightComponent
    {
        public LightComponent()
        {
            Type = LightType.Point;
            DiffuseColor = SpecularColor = AmbientColor = Vector3.One;
            DiffuseStrength = SpecularStrength = AmbientStrength = 1f;
            PositionOffset = Vector3.Zero;

            // completely quadratic attenuation (not ideal)
            Linear = Constant = 0f;
            Quadratic = 1f;
        }

        public LightType Type;
        public Vector3 DiffuseColor, SpecularColor, AmbientColor;
        public float DiffuseStrength, SpecularStrength, AmbientStrength;
        public Vector3 PositionOffset;
        public float Quadratic, Linear, Constant; // attenuation coefficients

        internal bool OnEvent(ComponentEventInfo eventInfo)
        {
            if (eventInfo.Event != ComponentEventID.Edited)
            {
                return false;
            }

            if (ImGui.BeginCombo("Type", Type.ToString()))
            {
                var lightTypes = Enum.GetValues<LightType>();
                foreach (var type in lightTypes)
                {
                    bool isSelected = Type == type;
                    if (ImGui.Selectable(type.ToString(), isSelected))
                    {
                        Type = type;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }

            // diffuse
            ImGui.SliderFloat("##diffuse-strength", ref DiffuseStrength, 0f, 1f);
            ImGui.SameLine();
            ImGui.ColorEdit3("Diffuse", ref DiffuseColor);

            // specular
            ImGui.SliderFloat("##specular-strength", ref SpecularStrength, 0f, 1f);
            ImGui.SameLine();
            ImGui.ColorEdit3("Specular", ref SpecularColor);

            // ambient
            ImGui.SliderFloat("##ambient-strength", ref AmbientStrength, 0f, 1f);
            ImGui.SameLine();
            ImGui.ColorEdit3("Ambient", ref AmbientColor);

            // no conditional, only point light is implemented
            ImGui.DragFloat3("Position offset", ref PositionOffset, 0.05f);

            // attenuation, not shown if light has no position (e.g. directional light)
            const float attenuationDragSpeed = 0.1f;
            ImGui.DragFloat("Quadratic", ref Quadratic, attenuationDragSpeed);
            ImGui.DragFloat("Linear", ref Linear, attenuationDragSpeed);
            ImGui.DragFloat("Constant", ref Constant, attenuationDragSpeed);

            return true;
        }
    }
}