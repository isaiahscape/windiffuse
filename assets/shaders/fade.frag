#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D u_texture;
uniform sampler2D u_targetTexture;
uniform float u_lerp;

varying vec4 v_color;
varying vec2 v_texCoord;


void main() {
    vec4 color = texture2D(u_texture, v_texCoord);
    vec4 colorTarget = texture2D(u_targetTexture, v_texCoord);
    float diff = length(color.xyz - colorTarget.xyz);
    vec4 finalColor = vec4(mix(color.xyz,colorTarget.xyz, max(u_lerp,min(0.01/diff,1.0))),1.0);
    gl_FragColor = finalColor;
}