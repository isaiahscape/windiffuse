#ifdef GL_ES
precision highp float;
#endif

uniform sampler2D u_texture;
uniform sampler2D u_rand;
//uniform float u_time;
uniform float u_timeScaled;
uniform vec2 u_resolution;
uniform float u_border;
uniform float u_scale;
uniform int u_pre_compute;

varying vec4 v_color;
varying vec2 v_texCoord;


float random (in vec2 _st) {
//    return fract(sin(dot(_st.xy,
//    vec2(12.9898,78.233)))*
//    43758.5453123);
    float a = 12.9898;
    float b = 78.233;
    float c = 43758.5453;
    float dt= dot(_st.xy ,vec2(a,b));
    float sn= mod(dt,3.14);
    return fract(sin(sn) * c);
}

// Based on Morgan McGuire @morgan3d
// https://www.shadertoy.com/view/4dS3Wd
float noise (in vec2 _st) {
    if (u_pre_compute == 0)
    {
        vec2 i = floor(_st);
        vec2 f = fract(_st);

        // Four corners in 2D of a tile
        float a = random(i);
        float b = random(i + vec2(1.0, 0.0));
        float c = random(i + vec2(0.0, 1.0));
        float d = random(i + vec2(1.0, 1.0));

        vec2 u = f * f * (3.0 - 2.0 * f);

        return mix(a, b, u.x) +
        (c - a)* u.y * (1.0 - u.x) +
        (d - b) * u.x * u.y;

    }else
    {
        return texture2D(u_rand,_st/8.0).r;
    }
}


float fbm ( in vec2 _st) {
    float v = 0.0;
    float a = 0.5;
    vec2 shift = vec2(1.0);
    shift = _st * 0.5;
    // Rotate to reduce axial bias
    mat2 rot = mat2(cos(0.5), sin(0.5),
    -sin(0.5), cos(0.50));
    for (int i = 0; i < 3; ++i) {
        v += a * noise(_st);
        _st = rot * _st * 1.3 + shift;
        a *= 0.5;
    }
    return v;
}

void main()
{
    vec2 uv = v_texCoord;
    if (u_resolution.x > u_resolution.y)
    {
        uv.x = uv.x * (u_resolution.x / u_resolution.y);
    }else
    {
        uv.y = uv.y / (u_resolution.x / u_resolution.y);
    }
    vec2 q = vec2( fbm( uv + vec2(u_timeScaled*0.1, cos(u_timeScaled)) ), fbm( uv + vec2(u_timeScaled*0.1,u_timeScaled) ) );

    vec2 r = vec2(
    fbm( 0.5*uv + 2.0*q + vec2(u_timeScaled*0.05,0.0) ),
    fbm( 0.5*uv + 2.0*q + vec2(u_timeScaled*0.1,0.0) )
    );

    r = (r-vec2(0.5,0.5)) * (u_scale + u_border) + vec2(0.5,0.5); // make uv to corner

    vec4 color = texture2D(u_texture, r);
    gl_FragColor = color;
}