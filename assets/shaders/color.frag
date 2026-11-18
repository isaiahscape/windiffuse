#ifdef GL_ES
precision highp float;
#endif

uniform sampler2D u_texture;
varying vec4 v_color;
varying vec2 v_texCoord;

void main() {
   vec4 color = texture2D(u_texture, v_texCoord);
   gl_FragColor = color;
}