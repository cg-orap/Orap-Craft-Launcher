export type ThemeId = 'rock' | 'tundra' | 'amber'

export interface ThemePreset {
  id: ThemeId
  name: string
  desc: string
  swatch: [string, string, string]
}

export const themes: ThemePreset[] = [
  {
    id: 'rock',
    name: '深岩',
    desc: '默认。冷静的中性灰，长时间使用不刺眼。',
    swatch: ['#0F1115', '#6FBF73', '#D9A85C'],
  },
  {
    id: 'tundra',
    name: '苔原',
    desc: '偏冷的青灰调，绿色更沉，适合夜间。',
    swatch: ['#0D1214', '#67B8A4', '#CFD8A4'],
  },
  {
    id: 'amber',
    name: '琥珀',
    desc: '暖砂棕底 + 琥珀主色，木质工具台的感觉。',
    swatch: ['#14110D', '#D3A05A', '#D98A5C'],
  },
]

export function applyTheme(id: ThemeId) {
  const root = document.documentElement
  if (id === 'rock') delete root.dataset.theme
  else root.dataset.theme = id
}