#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D u_texture;
uniform int u_level;
uniform vec2 u_resolution;
uniform float u_brightnessOffset;

varying vec4 v_color;
varying vec2 v_texCoord;

vec4 reSample(sampler2D texture, vec2 res, in int d, in vec2 uv)
{
    vec2 step1 = (vec2(d) + 0.5) / res;
    vec4 color = vec4(0.0);
    color += texture2D(texture, uv + step1) / float(4);
    color += texture2D(texture,  uv - step1) / float(4);
    vec2 step2 = step1;
    step2.x = -step2.x;
    color += texture2D(texture, uv + step2) / float(4);
    color += texture2D(texture,  uv - step2) / float(4);
    color.xyz *= (1.0 + u_brightnessOffset);
    return color;
}


void main() {
    gl_FragColor = reSample(u_texture, u_resolution, u_level, v_texCoord);
}