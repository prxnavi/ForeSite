// Unlit two-tone shader for Atlas map pieces.
// Top faces get the full color, side faces a darker shade, which gives the
// "extruded card" look from the design mockups without needing scene lights.
// No LightMode tag, so it renders in both the built-in pipeline and URP.
// Single-pass-instanced stereo safe for Quest.
Shader "Atlas/Block"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Emission ("Emission", Color) = (0,0,0,0)
        _SideShade ("Side shade", Range(0,1)) = 0.62
        _Flat ("Flat (ignore normals)", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent-50" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite On
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            fixed4 _Emission;
            float _SideShade;
            float _Flat;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 n : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.n = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.n);
                float up = saturate(n.y);
                // x-facing sides slightly darker than z-facing sides for depth
                float side = _SideShade - abs(n.x) * 0.08;
                float shade = lerp(lerp(side, 1.0, up), 1.0, _Flat);
                fixed3 c = _Color.rgb * shade + _Emission.rgb;
                return fixed4(c, _Color.a);
            }
            ENDCG
        }
    }
}
