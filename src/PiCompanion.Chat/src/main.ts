import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import './typography.css'
import './color-tokens.css'
import './component-tokens.css'
import './ui-components.css'
import './styles.css'

const app = createApp(App)
app.use(createPinia()).mount('#app')
