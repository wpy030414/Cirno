// src/fixture.ts — 测试共用：确定性 tokens fixture（46 key × light/dark）
import type { Tokens } from '@skins/core'

const NEUTRAL_L = ['#f7f7f7', '#ececf0', '#dcdce2', '#c9c9d2', '#b3b3bf', '#9a9aa8', '#82828f', '#6b6b78', '#565662', '#43434d', '#303038', '#232329']
const NEUTRAL_D = ['#ffffff', '#f0f0f2', '#d9d9de', '#b8b8c0', '#9898a2', '#7a7a85', '#63636d', '#4d4d55', '#39393f', '#27272c', '#1c1c20', '#121215']

export function fixtureTokens(): { light: Tokens; dark: Tokens } {
  const neutral = (dark: boolean): Record<string, string> => {
    const scale = dark ? NEUTRAL_D : NEUTRAL_L
    return Object.fromEntries(scale.map((v, i) => ['neutral-' + (i === 0 ? 50 : i * 100), v]))
  }
  const build = (dark: boolean): Tokens => {
    const n = neutral(dark)
    const light = !dark
    return {
      ...n,
      'brand-100': light ? '#d8e2f8' : '#16294f',
      'brand-300': light ? '#a9c2ee' : '#27457f',
      'brand-500': light ? '#5b7fd0' : '#6c92e0',
      'brand-600': light ? '#3f63b5' : '#8aa8ec',
      'brand-700': light ? '#2f4c8d' : '#a3bcf2',
      'brand-900': light ? '#1b2e57' : '#d5e2fb',
      'success-weak': light ? '#e3f3e7' : '#16301f',
      'success-base': light ? '#3f9d62' : '#5dbb85',
      'success-strong': light ? '#216b3e' : '#8edfb3',
      'warning-weak': light ? '#fbf0da' : '#332713',
      'warning-base': light ? '#c9a04e' : '#d9b566',
      'warning-strong': light ? '#8a6423' : '#edd9a8',
      'critical-weak': light ? '#fbe4e1' : '#381512',
      'critical-base': light ? '#d05a4e' : '#e07a6e',
      'critical-strong': light ? '#8f2f26' : '#f2aca4',
      'info-weak': light ? '#e2ecf7' : '#14283d',
      'info-base': light ? '#5b86b8' : '#77a6d8',
      'info-strong': light ? '#33557c' : '#a9c8ec',
      'text-strong': light ? '#1b1d22' : '#f2f3f5',
      'text-base': light ? '#3a3d46' : '#d7d9de',
      'text-weak': light ? '#6a6e7a' : '#a3a7b0',
      'text-weaker': light ? '#9a9ea9' : '#6f737c',
      'text-inverse': light ? '#f5f6f8' : '#17181c',
      'surface-base': light ? '#f7f7f9' : '#1a1b20',
      'surface-raised': light ? '#ffffff' : '#24252c',
      'surface-strong': light ? '#e4e6ec' : '#30323b',
      'surface-weak': light ? '#f0f1f5' : '#141519',
      'border-base': light ? '#c8cbd4' : '#3a3c45',
      'border-weak': light ? '#e0e2e8' : '#2a2b31',
      'border-strong': light ? '#9a9eab' : '#555864',
      'border-focus': light ? '#3f63b5' : '#8aa8ec',
      'input-base': light ? '#ffffff' : '#1f2026',
      'input-active': light ? '#eef2fb' : '#2a2c35',
      accent: light ? '#8a5fc0' : '#b49ae0',
    } as Tokens
  }
  return { light: build(false), dark: build(true) }
}
