import { createFileRoute, useNavigate } from '@tanstack/react-router'
import { useState } from 'react'
import { login } from '#/lib/api'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'

export const Route = createFileRoute('/login')({
  component: LoginPage,
})

function LoginPage() {
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    const ok = await login(email, password).catch(() => false)
    setBusy(false)
    if (!ok) {
      setError('E-posta veya parola hatalı.')
      return
    }
    navigate({ to: '/' })
  }

  return (
    <main className="flex min-h-[70vh] items-center justify-center px-4">
      <Card className="w-full max-w-sm">
        <CardHeader>
          <div className="mb-1 flex items-center gap-2">
            <span className="bg-primary h-2 w-2 rounded-full" />
            <CardTitle className="text-lg">Veil</CardTitle>
          </div>
          <p className="text-muted-foreground text-sm">Kontrol düzlemine giriş yapın</p>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-4">
            <div className="space-y-1.5">
              <label htmlFor="email" className="text-sm font-medium">
                E-posta
              </label>
              <Input
                id="email"
                type="email"
                required
                value={email}
                onChange={(e) => setEmail(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <label htmlFor="password" className="text-sm font-medium">
                Parola
              </label>
              <Input
                id="password"
                type="password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </div>
            {error && <p className="text-destructive text-sm">{error}</p>}
            <Button type="submit" disabled={busy} className="w-full">
              {busy ? 'Giriş yapılıyor…' : 'Giriş yap'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </main>
  )
}
