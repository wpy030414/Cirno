// packages/skin-core/src/extract-colors.ts
// Extract dominant colors from images using k-means clustering.
import sharp from 'sharp'
import type { RGB, ColorCluster } from './types.js'

/**
 * Convert RGB to L* (lightness) in [0, 100].
 * Simplified formula from CIE LAB color space.
 */
export function rgbToLStar(rgb: RGB): number {
  const [r, g, b] = rgb.map((c) => c / 255)

  // sRGB to linear
  const rLinear = r > 0.04045 ? Math.pow((r + 0.055) / 1.055, 2.4) : r / 12.92
  const gLinear = g > 0.04045 ? Math.pow((g + 0.055) / 1.055, 2.4) : g / 12.92
  const bLinear = b > 0.04045 ? Math.pow((b + 0.055) / 1.055, 2.4) : b / 12.92

  // Y (luminance)
  const y = 0.2126 * rLinear + 0.7152 * gLinear + 0.0722 * bLinear

  // L*
  const lStar = y > 0.008856 ? 116 * Math.pow(y, 1 / 3) - 16 : 903.3 * y
  return lStar
}

/**
 * Squared Euclidean distance between two RGB colors.
 */
function rgbDistance(a: RGB, b: RGB): number {
  return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2
}

/**
 * Deterministic PRNG (mulberry32) — k-means++ initialization must be
 * reproducible so the same input images always produce the same tokens.
 */
function mulberry32(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a |= 0
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/** Derive a stable seed from pixel data (first few pixels). */
function seedFromPixels(pixels: RGB[]): number {
  let seed = 2166136261
  const n = Math.min(pixels.length, 64)
  for (let i = 0; i < n; i++) {
    seed = Math.imul(seed ^ pixels[i][0], 16777619)
    seed = Math.imul(seed ^ pixels[i][1], 16777619)
    seed = Math.imul(seed ^ pixels[i][2], 16777619)
  }
  return seed >>> 0
}

/**
 * K-means clustering on an array of RGB colors.
 * Deterministic: seeded PRNG, same input → same output.
 */
function kMeans(pixels: RGB[], k: number, maxIterations = 100): RGB[] {
  if (pixels.length === 0) return []
  if (pixels.length <= k) return pixels

  const rand = mulberry32(seedFromPixels(pixels))

  // Initialize centroids using k-means++
  const centroids: RGB[] = [pixels[Math.floor(rand() * pixels.length)]]

  while (centroids.length < k) {
    const distances = pixels.map((pixel) => {
      const minDist = Math.min(...centroids.map((c) => rgbDistance(pixel, c)))
      return minDist
    })
    const totalDist = distances.reduce((sum, d) => sum + d, 0)
    const threshold = rand() * totalDist
    let cumulative = 0
    for (let i = 0; i < pixels.length; i++) {
      cumulative += distances[i]
      if (cumulative >= threshold) {
        centroids.push(pixels[i])
        break
      }
    }
  }

  // Iterate
  for (let iter = 0; iter < maxIterations; iter++) {
    // Assign pixels to nearest centroid
    const clusters: RGB[][] = Array.from({ length: k }, () => [])
    for (const pixel of pixels) {
      let minDist = Infinity
      let minIdx = 0
      for (let i = 0; i < k; i++) {
        const dist = rgbDistance(pixel, centroids[i])
        if (dist < minDist) {
          minDist = dist
          minIdx = i
        }
      }
      clusters[minIdx].push(pixel)
    }

    // Update centroids
    let converged = true
    for (let i = 0; i < k; i++) {
      if (clusters[i].length === 0) continue
      const newCentroid: RGB = [
        Math.round(clusters[i].reduce((sum, p) => sum + p[0], 0) / clusters[i].length),
        Math.round(clusters[i].reduce((sum, p) => sum + p[1], 0) / clusters[i].length),
        Math.round(clusters[i].reduce((sum, p) => sum + p[2], 0) / clusters[i].length),
      ]
      if (rgbDistance(newCentroid, centroids[i]) > 1) {
        converged = false
      }
      centroids[i] = newCentroid
    }

    if (converged) break
  }

  return centroids
}

/**
 * Extract dominant colors from an image using k-means clustering.
 *
 * @param imagePath - Path to the image file (webp/png/jpg)
 * @param k - Number of color clusters to extract (default: 16)
 * @returns Array of color clusters, sorted by brightness (darkest first)
 */
export async function extractColors(imagePath: string, k = 16): Promise<ColorCluster[]> {
  // Read image and resize for performance
  const image = sharp(imagePath).resize(100, 100, { fit: 'inside' }).removeAlpha()

  const { data, info } = await image.raw().toBuffer({ resolveWithObject: true })

  // Sample pixels (every 2nd pixel for speed)
  const pixels: RGB[] = []
  for (let i = 0; i < data.length; i += 6) {
    pixels.push([data[i], data[i + 1], data[i + 2]])
  }

  // Run k-means
  const centroids = kMeans(pixels, k)

  // Count pixels per centroid
  const counts = new Array(k).fill(0)
  for (const pixel of pixels) {
    let minDist = Infinity
    let minIdx = 0
    for (let i = 0; i < k; i++) {
      const dist = rgbDistance(pixel, centroids[i])
      if (dist < minDist) {
        minDist = dist
        minIdx = i
      }
    }
    counts[minIdx]++
  }

  // Build clusters with brightness
  const clusters: ColorCluster[] = centroids.map((color, i) => ({
    color,
    count: counts[i],
    brightness: rgbToLStar(color),
  }))

  // Sort by brightness (darkest first)
  clusters.sort((a, b) => a.brightness - b.brightness)

  return clusters
}

/**
 * Extract colors from multiple images and merge the results.
 */
export async function extractColorsFromImages(
  imagePaths: string[],
  k = 16
): Promise<ColorCluster[]> {
  const allClusters: ColorCluster[][] = await Promise.all(
    imagePaths.map((path) => extractColors(path, k))
  )

  // Flatten and re-cluster to get final palette
  const allColors: RGB[] = allClusters.flat().map((c) => c.color)
  const finalCentroids = kMeans(allColors, k)

  // Count occurrences
  const counts = new Array(k).fill(0)
  for (const color of allColors) {
    let minDist = Infinity
    let minIdx = 0
    for (let i = 0; i < k; i++) {
      const dist = rgbDistance(color, finalCentroids[i])
      if (dist < minDist) {
        minDist = dist
        minIdx = i
      }
    }
    counts[minIdx]++
  }

  // Build final clusters
  const clusters: ColorCluster[] = finalCentroids.map((color, i) => ({
    color,
    count: counts[i],
    brightness: rgbToLStar(color),
  }))

  clusters.sort((a, b) => a.brightness - b.brightness)

  return clusters
}
