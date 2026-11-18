#ifdef GL_ES
precision mediump float;
#endif

uniform sampler2D u_texture;
uniform float u_numSegments; // Number of segments
uniform vec2 u_resolution;
uniform float u_time;
varying vec4 v_color;
varying vec2 v_texCoord;

void main() {
    // Define the custom uniforms as constants or use ShaderToy's UI for uniforms
    const float u_inputOutputRatio = 1.8; // Ratio between input and output segments
    const float u_overlap = 0.8; // Overlap between segments
    const float u_light_strength = 0.10;

    // Calculate UV coordinates
    vec2 uv = v_texCoord;

    float segmentWidth = 1.0 / u_numSegments;
    float inputSegmentWidth = segmentWidth * u_inputOutputRatio;
    float overlapWidth = segmentWidth * u_overlap;

    // Determine which segment we are in
    float segmentIndex = floor(uv.x / segmentWidth);
    float segmentStart = segmentIndex * segmentWidth;
    float segmentEnd = segmentStart + segmentWidth;

    // Calculate the local uv within the segment
    float localUVx = (uv.x - segmentStart) / segmentWidth;

    // Apply log compression to the x coordinate within the segment
    float compressedX = log(1.0 + localUVx * 9.0) / log(10.0);

    // Calculate the corresponding input UV
    float inputSegmentStart = segmentIndex * (inputSegmentWidth - overlapWidth);
    vec2 inputUV = vec2(inputSegmentStart + compressedX * inputSegmentWidth, uv.y);

    // Get the color from the input image
    vec4 color = texture2D(u_texture, inputUV);

    // Apply the vertical gradient
    float gradientMidpoint = 0.8;
    float gradientStrength = smoothstep(gradientMidpoint, 1.0, uv.y);
    color = mix(color, vec4(0.0, 0.0, 0.0, 0.5), gradientStrength*0.5);

    // Apply the black gradient on the right side of each segment
    float rightGradientStrength = smoothstep(0.8, 1.0, localUVx);
    color = mix(color, vec4(0.0, 0.0, 0.0, rightGradientStrength), rightGradientStrength*u_light_strength);

    // Apply the white gradient on the left side of each segment
    float leftGradientStrength = smoothstep(0.1, 0.0, localUVx);
    color = mix(color, vec4(1.0, 1.0, 1.0, leftGradientStrength), leftGradientStrength*u_light_strength);

    // TODO: add luma based film noise

    gl_FragColor = color;
}

