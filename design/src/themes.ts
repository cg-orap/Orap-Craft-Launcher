export type ThemeId = 'rock' | 'frost' | 'paper'

export interface ThemePreset {
  id: ThemeId
  name: string
  desc: string
  swatch: [string, string, string]
  light?: boolean
}

export const themes: ThemePreset[] = [
  {
    id: 'rock',
    name: '深岩',
    desc: '默认。深色岩灰底 + 苔藓绿，夜间与长时间游玩更舒服。',
    swatch: ['#0F1115', '#6FBF73', '#D9A85C'],
  },
  {
    id: 'frost',
    name: '霜白',
    desc: '浅色冷调。白卡片 + 墨绿主色，白天使用层次清晰。',
    swatch: ['#EEF1F5', '#1F8A5B', '#A46518'],
    light: true,
  },
  {
    id: 'paper',
    name: '暖纸',
    desc: '浅色暖调。米白纸感 + 赭石主色，像书桌上的工具箱。',
    swatch: ['#F3EDE2', '#9B5F2A', '#3C6F8F'],
    light: true,
  },
]

export function applyTheme(id: ThemeId) {
  const root = document.documentElement
  if (id === 'rock') delete root.dataset.theme
  else root.dataset.theme = id
}