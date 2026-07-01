import { useState } from 'react'
import Login from './pages/Login'
import Cockpit from './pages/Cockpit'

export default function App() {
  const [authed, setAuthed] = useState(() => !!localStorage.getItem('td_token'))
  return authed ? <Cockpit /> : <Login onLogin={() => setAuthed(true)} />
}
